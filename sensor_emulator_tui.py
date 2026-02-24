#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Sensor Data Emulator with Console TUI Interface for JSQ Application (Windows compatible)

Эмулятор передачи данных от комплекса датчиков с псевдо-графическим интерфейсом.
Отображает текущие значения по каждому каналу и состояние оборудования.

Работает на Windows и Linux без библиотеки curses.

Формат пакета телеметрии:
  [4 bytes BE: total_length][payload][4 bytes BE: trailer]
  payload:
    - offset 0-19: header (20 bytes)
    - offset 20-23: N values count (BE u32)
    - offset 24+: N float64 values (BE)

Формат управляющих команд (входящие):
  - Короткие команды: [4 bytes BE: length][2 bytes: code][2 bytes: value]
  - DO команды: [4 bytes BE: length][8 bytes header][4 bytes ASCII "DOxx"][2 bytes value]

Usage:
  python sensor_emulator_tui.py --config config.full.json --host 0.0.0.0 --port 55555 --rate 2
"""

import argparse
import json
import random
import socket
import struct
import sys
import threading
import time
from datetime import datetime
from typing import Dict, List, Optional, Tuple


# ANSI коды для консоли
class ANSI:
    RESET = "\033[0m"
    BOLD = "\033[1m"
    DIM = "\033[2m"
    
    # Цвета текста
    BLACK = "\033[30m"
    RED = "\033[31m"
    GREEN = "\033[32m"
    YELLOW = "\033[33m"
    BLUE = "\033[34m"
    MAGENTA = "\033[35m"
    CYAN = "\033[36m"
    WHITE = "\033[37m"
    
    # Цвета фона
    BG_RED = "\033[41m"
    BG_GREEN = "\033[42m"
    BG_YELLOW = "\033[43m"
    BG_BLUE = "\033[44m"
    
    # Управление курсором
    UP = "\033[1A"
    DOWN = "\033[1B"
    RIGHT = "\033[1C"
    LEFT = "\033[1D"
    HOME = "\033[H"
    CLEAR = "\033[H\033[2J"  # Переместить в начало + очистить экран
    CLEAR_LINE = "\033[2K"
    
    @staticmethod
    def color(r, g, b):
        return f"\033[38;2;{r};{g};{b}m"
    
    @staticmethod
    def bg_color(r, g, b):
        return f"\033[48;2;{r};{g};{b}m"
    
    @staticmethod
    def move_to(row, col):
        return f"\033[{row};{col}H"


class EquipmentState:
    """Состояние оборудования."""
    def __init__(self, name: str, is_on: bool = False):
        self.name = name
        self.is_on = is_on
        self.last_change: float = time.time()
        self.change_count: int = 0

    def set_state(self, is_on: bool) -> None:
        if self.is_on != is_on:
            self.is_on = is_on
            self.last_change = time.time()
            self.change_count += 1


class ChannelConfig:
    """Конфигурация одного канала + генератор значений с дребезгом и отказами.

    Требования:
      - Данные передаются только для активных каналов (is_active=True).
        Неактивные каналы отдают -99.0 (как «нет датчика»).
      - Значения квантуются до 3 знаков после запятой.
      - С вероятностью anomaly_chance (по умолчанию 0.02 = 2%) активный датчик может:
          * «отключиться» (передавать -99.0) на небольшое время
          * «сломаться» (передавать явно неестественные/аномальные значения) на небольшое время
    """

    FAULT_NORMAL = "NORMAL"
    FAULT_DISCONNECTED = "DISCONNECTED"
    FAULT_FAILED = "FAILED"

    def __init__(
        self,
        name: str,
        min_val: float,
        max_val: float,
        unit: str = "",
        group: str = "",
        description: str = "",
        is_active: bool = True,
        anomaly_chance: float = 0.02,
        noise_factor: float = 0.01,
        drift_factor: float = 0.001,
    ):
        self.name = name
        self.min_val = float(min_val)
        self.max_val = float(max_val)
        self.unit = unit
        self.group = group
        self.description = description
        self.is_active = bool(is_active)
        self.anomaly_chance = float(anomaly_chance)
        self.noise_factor = float(noise_factor)
        self.drift_factor = float(drift_factor)

        # Базовый уровень (медленный дрейф) + коррелированный шум (аналоговый дребезг)
        self.current_value = (self.min_val + self.max_val) / 2.0
        self._noise_state = 0.0

        # Режим отказа
        self.fault_mode = self.FAULT_NORMAL
        self.fault_until = 0.0
        self.failure_kind = ""
        self.failure_value = 0.0

        # Последнее отправленное значение
        self.last_value = self._quantize(self.current_value) if self.is_active else -99.0

    def set_active(self, is_active: bool) -> None:
        """Включает/выключает канал (для TUI toggle)."""
        self.is_active = bool(is_active)
        if not self.is_active:
            self.fault_mode = self.FAULT_NORMAL
            self.fault_until = 0.0
            self.failure_kind = ""
            self.failure_value = 0.0
            self._noise_state = 0.0
            self.last_value = -99.0
        else:
            # Мягкий «старт» в середину диапазона
            self.current_value = (self.min_val + self.max_val) / 2.0
            self._noise_state = 0.0
            self.fault_mode = self.FAULT_NORMAL
            self.fault_until = 0.0
            self.failure_kind = ""
            self.failure_value = 0.0
            self.last_value = self._quantize(self.current_value)

    def _range(self) -> float:
        r = self.max_val - self.min_val
        return r if r != 0 else 1.0

    @staticmethod
    def _quantize(v: float) -> float:
        # В пакете всё равно float64, но квантуем до 0.001
        return float(f"{v:.3f}")

    def _start_fault(self, now: float) -> None:
        """Запускает отказ на небольшой интервал времени."""
        duration = random.uniform(3.0, 25.0)

        if random.random() < 0.5:
            # 50% — отключение
            self.fault_mode = self.FAULT_DISCONNECTED
            self.fault_until = now + duration
            self.failure_kind = ""
            self.failure_value = 0.0
            return

        # 50% — поломка (неестественные данные)
        self.fault_mode = self.FAULT_FAILED
        self.fault_until = now + duration

        r = self._range()
        kind = random.choice(["STUCK_HIGH", "STUCK_LOW", "SPIKES", "RANDOM_WALK"])
        self.failure_kind = kind

        if kind == "STUCK_HIGH":
            self.failure_value = self.max_val + random.uniform(0.2, 1.5) * r
        elif kind == "STUCK_LOW":
            self.failure_value = self.min_val - random.uniform(0.2, 1.5) * r
        elif kind == "RANDOM_WALK":
            sign = -1.0 if random.random() < 0.5 else 1.0
            self.failure_value = (self.min_val + self.max_val) / 2.0 + sign * random.uniform(1.0, 2.5) * r
        else:
            self.failure_value = 0.0

    def trigger_fault(self, now: Optional[float] = None) -> bool:
        """Запускает отказ принудительно (используется глобальным инжектором).
        Возвращает True, если отказ стартовал.
        """
        if now is None:
            now = time.time()
        if (not self.is_active) or (self.fault_mode != self.FAULT_NORMAL):
            return False
        self._start_fault(now)
        return True

    def _maybe_end_fault(self, now: float) -> None:
        if self.fault_mode != self.FAULT_NORMAL and now >= self.fault_until:
            self.fault_mode = self.FAULT_NORMAL
            self.fault_until = 0.0
            self.failure_kind = ""
            self.failure_value = 0.0
            self._noise_state = 0.0

    def _generate_normal_value(self) -> float:
        """Нормальный режим: дрейф + коррелированный шум (дребезг)."""
        r = self._range()

        # Медленный дрейф базы
        self.current_value += random.gauss(0.0, r * self.drift_factor)

        # Ограничиваем базу строго диапазоном
        if self.current_value < self.min_val:
            self.current_value = self.min_val
        elif self.current_value > self.max_val:
            self.current_value = self.max_val

        # Коррелированный шум (AR(1)), чтобы был «живой» дребезг
        alpha = 0.85
        sigma = r * self.noise_factor
        self._noise_state = alpha * self._noise_state + random.gauss(0.0, sigma * (1.0 - alpha))

        value = self.current_value + self._noise_state

        # Лёгкий clamp
        if value < self.min_val:
            value = self.min_val
        elif value > self.max_val:
            value = self.max_val

        return self._quantize(value)

    def _generate_failed_value(self) -> float:
        """Аварийный режим: «неестественные» значения."""
        r = self._range()
        center = (self.min_val + self.max_val) / 2.0

        if self.failure_kind in ("STUCK_HIGH", "STUCK_LOW"):
            return self._quantize(self.failure_value)

        if self.failure_kind == "RANDOM_WALK":
            self.failure_value += random.gauss(0.0, r * 0.35)
            return self._quantize(self.failure_value)

        # SPIKES
        if random.random() < 0.75:
            sign = -1.0 if random.random() < 0.5 else 1.0
            value = center + sign * random.uniform(1.0, 3.0) * r
        else:
            value = (self.min_val - random.uniform(0.2, 1.2) * r) if random.random() < 0.5 else (self.max_val + random.uniform(0.2, 1.2) * r)

        return self._quantize(value)

    def get_status(self) -> str:
        """Статус для TUI."""
        if not self.is_active:
            return "INACTIVE"
        if self.fault_mode == self.FAULT_DISCONNECTED:
            return "OFFLINE"
        if self.fault_mode == self.FAULT_FAILED:
            return "FAILED"
        return "ACTIVE"

    def generate_value(self, now: Optional[float] = None) -> float:
        """Генерирует следующее значение канала."""
        if now is None:
            now = time.time()

        if not self.is_active:
            self.last_value = -99.0
            return self.last_value

        self._maybe_end_fault(now)


        if self.fault_mode == self.FAULT_DISCONNECTED:
            self.last_value = -99.0
            return self.last_value

        if self.fault_mode == self.FAULT_FAILED:
            self.last_value = self._generate_failed_value()
            return self.last_value

        self.last_value = self._generate_normal_value()
        return self.last_value

    def __repr__(self) -> str:
        return f"ChannelConfig({self.name}, min={self.min_val}, max={self.max_val})"


class ControlCommandHandler:

    """Обработчик управляющих команд."""
    
    # Коды команд из анализа трафика
    CMD_CAMERA_POWER = 0x11
    CMD_RECORDING_MODE = 0x15
    
    def __init__(self, equipment_states: Dict[str, EquipmentState]):
        self.equipment = equipment_states
        self.command_log: List[Tuple[str, str, str]] = []  # (timestamp, command, value)
        self.lock = threading.Lock()
    
    def parse_command(self, data: bytes) -> Optional[Tuple[str, str]]:
        """
        Парсит входящую команду.
        Returns: (command_name, value_str) или None
        """
        if len(data) < 8:
            return None
        
        # Короткая команда: [4 bytes length][2 bytes code][2 bytes value]
        if len(data) == 8:
            length = struct.unpack(">I", data[0:4])[0]
            code = struct.unpack(">H", data[4:6])[0]
            value = struct.unpack(">H", data[6:8])[0]
            
            if code == self.CMD_CAMERA_POWER:
                return ("CAMERA", "ON" if value == 0x0101 else "OFF")
            elif code == self.CMD_RECORDING_MODE:
                return ("RECORDING", "ON" if value == 0x0101 else "OFF")
        
        # DO команда: [4 bytes length][8 bytes header][4 bytes ASCII "DOxx"][2 bytes value]
        elif len(data) >= 20:
            try:
                # Ищем ASCII "DO" в пакете
                payload_start = 4  # после длины
                for i in range(payload_start, len(data) - 5):
                    if data[i:i+2] == b'DO':
                        do_name = data[i:i+4].decode('ascii', errors='ignore').strip()
                        value_bytes = data[i+4:i+6]
                        value = struct.unpack(">H", value_bytes)[0] if len(value_bytes) == 2 else 0
                        return (do_name, str(value))
            except:
                pass
        
        return None
    
    def handle_command(self, command_name: str, value_str: str) -> None:
        """Обрабатывает команду и обновляет состояние оборудования."""
        with self.lock:
            timestamp = datetime.now().strftime("%H:%M:%S")
            self.command_log.append((timestamp, command_name, value_str))
            
            # Ограничиваем размер лога
            if len(self.command_log) > 50:
                self.command_log = self.command_log[-50:]
            
            # Обновляем состояние оборудования
            if command_name == "CAMERA":
                self.equipment["CAMERA"].set_state(value_str == "ON")
            elif command_name == "RECORDING":
                self.equipment["RECORDING"].set_state(value_str == "ON")
            elif command_name.startswith("DO"):
                # DO01, DO02 и т.д.
                self.equipment[command_name].set_state(value_str != "0")
    
    def get_recent_commands(self, count: int = 10) -> List[Tuple[str, str, str]]:
        """Возвращает последние команды."""
        with self.lock:
            return self.command_log[-count:]


class SensorEmulatorTUI:
    """Эмулятор с консольным TUI интерфейсом (Windows compatible)."""

    def __init__(self, config_path: str):
        # Настройки соединения
        self.host = "0.0.0.0"
        self.port = 55555
        self.rate_hz = 2.0
        self.fault_global_chance = 0.02  # 2% шанс на один отказ за кадр

        # Каналы
        self.channels: List[ChannelConfig] = []

        # Статистика
        self.packets_sent = 0
        self.anomalies_generated = 0
        self.start_time: Optional[float] = None
        self.clients_connected = 0
        self.commands_received = 0

        # Состояние оборудования
        self.equipment: Dict[str, EquipmentState] = {
            "CAMERA": EquipmentState("Камера"),
            "RECORDING": EquipmentState("Запись"),
            "DO01": EquipmentState("DO01"),
            "DO02": EquipmentState("DO02"),
        }
        
        # Обработчик команд
        self.command_handler = ControlCommandHandler(self.equipment)

        # Блокировка для потокобезопасности
        self.data_lock = threading.Lock()

        # Флаги управления
        self.running = True
        self.client_connected = False

        # Навигация
        self.scroll_offset = 0
        
        # Буфер для ввода номера канала
        self.input_buffer = ""
        self.input_timeout = 0

        # Загружаем конфигурацию
        self.load_config(config_path)

    def load_config(self, config_path: str) -> None:
        """Загружает конфигурацию из JSON файла."""
        with open(config_path, "r", encoding="utf-8") as f:
            config = json.load(f)

        self.host = config.get("host", self.host)
        self.port = config.get("port", self.port)
        self.rate_hz = config.get("rate_hz", self.rate_hz)
        self.fault_global_chance = config.get("fault_global_chance", self.fault_global_chance)

        channels_data = config.get("channels", [])
        for ch in channels_data:
            channel = ChannelConfig(
                name=ch["name"],
                min_val=ch["min"],
                max_val=ch["max"],
                unit=ch.get("unit", ""),
                group=ch.get("group", ""),
                description=ch.get("description", ""),
                is_active=ch.get("is_active", True),
                anomaly_chance=ch.get("anomaly_chance", 0.02),
                noise_factor=ch.get("noise_factor", 0.01),
                drift_factor=ch.get("drift_factor", 0.001),
            )
            self.channels.append(channel)

        print(f"Загружено {len(self.channels)} каналов")

    def build_packet(self, values: List[float]) -> bytes:
        """Строит пакет в формате оригинального протокола."""
        n_values = len(values)

        header = bytearray(20)
        count_bytes = struct.pack(">I", n_values)
        values_bytes = b"".join(struct.pack(">d", v) for v in values)

        payload = bytes(header) + count_bytes + values_bytes
        total_length = len(payload) + 4
        trailer = struct.pack(">I", total_length)
        packet = struct.pack(">I", total_length) + payload + trailer

        return packet

    def generate_frame(self) -> Tuple[bytes, int]:
        """Генерирует кадр со значениями всех каналов.

        Протокол ожидает фиксированное число значений, поэтому даже неактивные/
        отвалившиеся каналы присутствуют в кадре, но отдают -99.0.
        """
        values: List[float] = []
        anomaly_count = 0
        now = time.time()

        with self.data_lock:
            # Глобальный инжектор отказов:
            # с вероятностью fault_global_chance выбираем ОДИН активный канал (в норме) и запускаем ему отказ.
            if self.fault_global_chance > 0 and random.random() < self.fault_global_chance:
                candidates = [ch for ch in self.channels if ch.is_active and ch.fault_mode == ch.FAULT_NORMAL]
                if candidates:
                    # anomaly_chance канала используется как вес (можно сделать одни датчики «хрупче», чем другие)
                    weights = [max(0.0, float(ch.anomaly_chance)) for ch in candidates]
                    if sum(weights) <= 0:
                        pick = random.choice(candidates)
                    else:
                        pick = random.choices(candidates, weights=weights, k=1)[0]
                    pick.trigger_fault(now=now)

            for channel in self.channels:
                value = channel.generate_value(now=now)

                # Аномалией считаем только для активных датчиков: -99 (отключился)
                # или явный выход за диапазон.
                if channel.is_active and (value == -99.0 or value < channel.min_val or value > channel.max_val):
                    anomaly_count += 1
                    self.anomalies_generated += 1

                values.append(value)

            packet = self.build_packet(values)
            self.packets_sent += 1

        return packet, anomaly_count

    def _get_terminal_size(self) -> Tuple[int, int]:
        """Получает размер терминала."""
        try:
            import shutil
            size = shutil.get_terminal_size((80, 24))
            return size.lines, size.columns
        except:
            return 24, 80

    def _render_screen(self) -> str:
        """Рендерит весь экран в строку."""
        lines = []
        height, width = self._get_terminal_size()
        
        # Заголовок
        title = " ═══════════════════════════════════════════════════════════════ "
        title_centered = " JSQ Sensor Emulator TUI "
        lines.append(f"{ANSI.CYAN}{ANSI.BOLD}{title_centered.center(width)[:width-1]}{ANSI.RESET}")
        
        # Статистика
        elapsed = time.time() - self.start_time if self.start_time else 0
        pps = self.packets_sent / max(elapsed, 1) if elapsed > 0 else 0
        status = "ONLINE" if self.client_connected else "WAITING"
        status_color = ANSI.GREEN if self.client_connected else ANSI.RED
        
        stats_line = f" Status: {status_color}{ANSI.BOLD}{status:<10}{ANSI.RESET}  Packets: {ANSI.GREEN}{self.packets_sent:<8}{ANSI.RESET} Anomalies: {ANSI.RED}{self.anomalies_generated:<6}{ANSI.RESET}"
        lines.append(stats_line[:width-1])
        
        stats_line2 = f" Rate: {pps:.1f} pkt/s  Channels: {len(self.channels)}  Clients: {self.clients_connected}  Uptime: {elapsed:.0f}s"
        lines.append(stats_line2[:width-1])
        
        # Разделитель
        lines.append(f"{ANSI.CYAN}{'─' * (width - 1)}{ANSI.RESET}")
        
        # Оборудование
        equip_line = f"{ANSI.CYAN}{ANSI.BOLD} EQUIPMENT {ANSI.RESET}"
        lines.append(equip_line)
        
        # Индикаторы оборудования
        equip_status = []
        for name, state in self.equipment.items():
            icon = "●" if state.is_on else "○"
            color = ANSI.YELLOW if state.is_on else ANSI.RED
            status_text = "ON" if state.is_on else "OFF"
            equip_status.append(f"{color}{icon} {name}: {status_text}{ANSI.RESET}")
        
        equip_status_line = "  ".join(equip_status)
        lines.append(f"  {equip_status_line}"[:width-1])
        
        # Разделитель
        lines.append(f"{ANSI.CYAN}{'─' * (width - 1)}{ANSI.RESET}")
        
        # Последние команды
        cmd_header = f"{ANSI.CYAN}{ANSI.BOLD} RECENT COMMANDS {ANSI.RESET}"
        lines.append(cmd_header)
        
        recent_cmds = self.command_handler.get_recent_commands(3)
        if recent_cmds:
            for ts, cmd, val in recent_cmds[-3:]:
                cmd_str = f"  {ANSI.MAGENTA}[{ts}]{ANSI.RESET} {ANSI.CYAN}{cmd}{ANSI.RESET} = {ANSI.YELLOW}{val}{ANSI.RESET}"
                lines.append(cmd_str[:width-1])
        else:
            lines.append(f"  {ANSI.DIM}No commands yet...{ANSI.RESET}")
        
        # Разделитель
        lines.append(f"{ANSI.CYAN}{'─' * (width - 1)}{ANSI.RESET}")
        
        # Каналы данных
        channels_header = f"{ANSI.CYAN}{ANSI.BOLD} CHANNELS DATA {ANSI.RESET}{ANSI.DIM}(↑↓ scroll, q quit, r reset){ANSI.RESET}"
        lines.append(channels_header)

        # Заголовки колонок
        header_line = f"  {ANSI.CYAN}{'#':<4} {'Name':<12} {'Group':<8} {'Value':>15} {'Unit':<8} {'Status':<10}{ANSI.RESET}"
        lines.append(header_line[:width-1])

        # Каналы - резервируем место для нижней панели (6 строк: 2 заголовка + 4 низ)
        # Всего строк до каналов: 14 (заголовок + 2 стат + разделитель + 2 оборуд + разделитель + 3 команды + разделитель + 2 заголовка каналов)
        # Нижняя панель: 4 строки
        # Итого резерв: 14 + 4 = 18
        reserved_lines = 18
        max_visible = height - reserved_lines
        
        with self.data_lock:
            visible_channels = self.channels[self.scroll_offset:self.scroll_offset + max_visible]
            
            for i, channel in enumerate(visible_channels):
                actual_idx = self.scroll_offset + i
                value = channel.last_value

                # Форматируем значение: всегда 3 знака после запятой
                value_str = f"{value:>12.3f}"

                status = channel.get_status()
                is_anomaly = status in ("OFFLINE", "FAILED") or (channel.is_active and (value < channel.min_val or value > channel.max_val))

                if is_anomaly:
                    color = ANSI.RED
                elif channel.is_active:
                    color = ANSI.GREEN
                else:
                    color = ANSI.DIM

                line = f"  {color}{actual_idx + 1:<4} {channel.name:<12} {channel.group:<8} {value_str} {channel.unit:<8} {status:<10}{ANSI.RESET}"
                lines.append(line[:width-1])

        # Нижняя панель - всегда 4 строки
        help_line = f" {ANSI.DIM}w/s:Scroll  a/d:Page  Home/End:First/Last  Space:Toggle ALL  t+#:Toggle #  q:Quit{ANSI.RESET}"
        lines.append(help_line[:width-1])
        
        # Инфо панель - всегда одинаковая высота
        info_line = f" {ANSI.DIM}Green=ACTIVE  Red=ANOMALY  Dim=INACTIVE  Yellow=Equipment ON{ANSI.RESET}"
        lines.append(info_line[:width-1])
        
        # Строка ввода номера канала (фиксированная высота)
        if self.input_buffer:
            input_display = f" {ANSI.YELLOW}{ANSI.BOLD}Toggle channel: {self.input_buffer}_ (Enter to confirm){ANSI.RESET}"
            lines.append(input_display[:width-1])
        else:
            # Пустая строка для сохранения высоты
            lines.append("")
        
        # Позиция прокрутки (фиксированная высота)
        scroll_info = f" {ANSI.DIM}Viewing: {self.scroll_offset + 1}-{min(self.scroll_offset + max(1, height - 22), len(self.channels))} of {len(self.channels)}{ANSI.RESET}"
        lines.append(scroll_info[:width-1])
        
        return "\n".join(lines)

    def _toggle_visible_channels(self) -> None:
        """Переключает состояние видимых каналов (ACTIVE <-> INACTIVE)."""
        with self.data_lock:
            # Переключаем все каналы
            for channel in self.channels:
                channel.set_active(not channel.is_active)
            # Не печатаем в консоль - это ломает TUI
    
    def _toggle_channel_by_index(self, index: int) -> None:
        """Переключает состояние канала по индексу (1-based)."""
        with self.data_lock:
            if 1 <= index <= len(self.channels):
                channel = self.channels[index - 1]
                channel.set_active(not channel.is_active)
                # Не печатаем в консоль - это ломает TUI

    def run_server(self, duration: float = 0) -> None:
        """Запускает TCP сервер и TUI."""
        # Запускаем сервер в отдельном потоке
        server_thread = threading.Thread(target=self._server_loop, daemon=True)
        server_thread.start()

        # Главный цикл TUI
        self.start_time = time.time()

        # Для Linux: используем select и termios
        import select
        import tty
        import termios
        
        # Сохраняем настройки терминала
        old_settings = termios.tcgetattr(sys.stdin)
        tty.setcbreak(sys.stdin.fileno())

        try:
            while self.running:
                # Обработка ввода (Linux)
                key_pressed = False
                if select.select([sys.stdin], [], [], 0.05)[0]:
                    key = sys.stdin.read(1)
                    key_pressed = True
                    
                    if key in ('q', 'Q'):
                        self.running = False
                        break
                    elif key in ('r', 'R'):
                        with self.data_lock:
                            self.packets_sent = 0
                            self.anomalies_generated = 0
                            self.start_time = time.time()
                    elif key in ('w', 'W'):  # Up
                        self.scroll_offset = max(0, self.scroll_offset - 5)
                    elif key in ('s', 'S'):  # Down
                        max_scroll = max(0, len(self.channels) - 1)
                        self.scroll_offset = min(max_scroll, self.scroll_offset + 5)
                    elif key in ('a', 'A'):  # Page Up
                        self.scroll_offset = max(0, self.scroll_offset - 20)
                    elif key in ('d', 'D'):  # Page Down
                        max_scroll = max(0, len(self.channels) - 1)
                        self.scroll_offset = min(max_scroll, self.scroll_offset + 20)
                    elif key == '\x1b':  # Escape sequence (стрелки)
                        if select.select([sys.stdin], [], [], 0.01)[0]:
                            key2 = sys.stdin.read(1)
                            if key2 == '[' and select.select([sys.stdin], [], [], 0.01)[0]:
                                key3 = sys.stdin.read(1)
                                if key3 == 'A':  # Up arrow
                                    self.scroll_offset = max(0, self.scroll_offset - 5)
                                elif key3 == 'B':  # Down arrow
                                    max_scroll = max(0, len(self.channels) - 1)
                                    self.scroll_offset = min(max_scroll, self.scroll_offset + 5)
                                elif key3 == '5':  # Page Up
                                    self.scroll_offset = max(0, self.scroll_offset - 20)
                                    if select.select([sys.stdin], [], [], 0.01)[0]:
                                        sys.stdin.read(1)  # ~
                                elif key3 == '6':  # Page Down
                                    max_scroll = max(0, len(self.channels) - 1)
                                    self.scroll_offset = min(max_scroll, self.scroll_offset + 20)
                                    if select.select([sys.stdin], [], [], 0.01)[0]:
                                        sys.stdin.read(1)  # ~
                                elif key3 == 'H':  # Home
                                    self.scroll_offset = 0
                                elif key3 == 'F':  # End
                                    self.scroll_offset = max(0, len(self.channels) - 1)
                    elif key == ' ':  # Пробел - переключить все каналы
                        self._toggle_visible_channels()
                    elif key in ('t', 'T'):  # Toggle по номеру
                        self.input_buffer = ""
                        self.input_timeout = time.time()
                    elif key in ('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'):
                        # Ввод номера канала
                        if self.input_buffer or (time.time() - self.input_timeout < 3):
                            self.input_buffer += key
                            self.input_timeout = time.time()
                            if len(self.input_buffer) >= 3:
                                try:
                                    idx = int(self.input_buffer)
                                    self._toggle_channel_by_index(idx)
                                except ValueError:
                                    pass
                                self.input_buffer = ""
                    elif key == '\r':  # Enter - подтвердить ввод
                        if self.input_buffer:
                            try:
                                idx = int(self.input_buffer)
                                self._toggle_channel_by_index(idx)
                            except ValueError:
                                pass
                            self.input_buffer = ""
                    elif key == '\x08':  # Backspace
                        self.input_buffer = self.input_buffer[:-1]
                        self.input_timeout = time.time()

                # Отрисовка
                screen = self._render_screen()
                # Полная очистка экрана и отрисовка заново
                sys.stdout.write(ANSI.CLEAR)  # \033[H\033[2J - в начало + очистить
                sys.stdout.write(screen)
                sys.stdout.flush()

                # Проверка длительности
                if duration > 0:
                    elapsed = time.time() - self.start_time
                    if elapsed >= duration:
                        print(f"\nЗавершение по таймеру ({duration}с)")
                        self.running = False
                        break

        except KeyboardInterrupt:
            self.running = False
        finally:
            # Восстанавливаем настройки терминала и показываем курсор
            termios.tcsetattr(sys.stdin, termios.TCSADRAIN, old_settings)
            sys.stdout.write("\033[?25h")  # Показать курсор
            sys.stdout.flush()

        self.running = False

    def _server_loop(self) -> None:
        """Цикл сервера (отдельный поток)."""
        server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        server_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server_socket.bind((self.host, self.port))
        server_socket.listen(5)
        server_socket.settimeout(1.0)

        while self.running:
            try:
                client_socket, addr = server_socket.accept()
                self.client_connected = True
                self.clients_connected += 1

                # Создаём поток для обработки клиента
                client_thread = threading.Thread(
                    target=self._handle_client,
                    args=(client_socket, addr),
                    daemon=True
                )
                client_thread.start()

            except socket.timeout:
                continue
            except OSError:
                break
            except Exception as e:
                if self.running:
                    pass

        server_socket.close()

    def _handle_client(self, client_socket: socket.socket, addr) -> None:
        """Обрабатывает подключение клиента."""
        try:
            client_socket.settimeout(5.0)

            # Запускаем поток для отправки телеметрии
            send_thread = threading.Thread(
                target=self._send_telemetry,
                args=(client_socket,),
                daemon=True
            )
            send_thread.start()

            # Принимаем команды от клиента
            buffer = b""
            while self.running:
                try:
                    data = client_socket.recv(4096)
                    if not data:
                        break

                    buffer += data

                    # Парсим команды из буфера
                    while len(buffer) >= 4:
                        # Первые 4 байта - длина
                        length = struct.unpack(">I", buffer[0:4])[0]

                        if len(buffer) < 4 + length:
                            break

                        # Извлекаем команду
                        command_data = buffer[4:4 + length]
                        buffer = buffer[4 + length:]

                        # Парсим и обрабатываем команду
                        result = self.command_handler.parse_command(command_data)
                        if result:
                            cmd_name, value_str = result
                            self.command_handler.handle_command(cmd_name, value_str)
                            self.commands_received += 1

                except socket.timeout:
                    continue
                except (ConnectionResetError, BrokenPipeError):
                    break

        except Exception as e:
            pass
        finally:
            try:
                client_socket.close()
            except:
                pass

            with self.data_lock:
                self.client_connected = False

    def _send_telemetry(self, client_socket: socket.socket) -> None:
        """Отправляет телеметрию клиенту."""
        interval = 1.0 / self.rate_hz

        while self.running and self.client_connected:
            try:
                start = time.time()

                packet, anomaly_count = self.generate_frame()
                client_socket.sendall(packet)

                # Ждём следующий интервал
                elapsed = time.time() - start
                sleep_time = interval - elapsed
                if sleep_time > 0:
                    time.sleep(sleep_time)

            except (ConnectionResetError, BrokenPipeError, OSError):
                break

    def print_stats(self) -> None:
        """Выводит статистику работы."""
        if self.start_time:
            duration = time.time() - self.start_time
            print(f"\n{ANSI.CYAN}=== Статистика ==={ANSI.RESET}")
            print(f"Длительность: {duration:.1f}с")
            print(f"Пакетов отправлено: {self.packets_sent}")
            print(f"Аномалий сгенерировано: {self.anomalies_generated}")
            print(f"Команд получено: {self.commands_received}")
            if self.packets_sent > 0:
                print(f"Средняя частота: {self.packets_sent / max(duration, 1):.1f} пакетов/с")


def main():
    parser = argparse.ArgumentParser(
        description="Эмулятор передатчика данных датчиков JSQ с TUI"
    )
    parser.add_argument(
        "--config",
        type=str,
        default="config.full.json",
        help="Путь к JSON конфигурации",
    )
    parser.add_argument(
        "--host",
        type=str,
        default="0.0.0.0",
        help="Хост для прослушивания",
    )
    parser.add_argument(
        "--port",
        type=int,
        default=55555,
        help="Порт для прослушивания",
    )
    parser.add_argument(
        "--rate",
        type=float,
        default=2.0,
        help="Частота передачи (пакетов в секунду)",
    )
    parser.add_argument(
        "--duration",
        type=float,
        default=0,
        help="Длительность работы в секундах (0 = бесконечно)",
    )

    args = parser.parse_args()

    # Создаём эмулятор
    emulator = SensorEmulatorTUI(args.config)

    # Применяем параметры командной строки
    emulator.host = args.host
    emulator.port = args.port
    emulator.rate_hz = args.rate

    # Запускаем с TUI
    print("Запуск эмулятора с TUI интерфейсом...")
    print(f"Слушаем {emulator.host}:{emulator.port}")
    print("Нажмите 'q' для выхода, ↑↓ для прокрутки, 'r' для сброса статистики")
    
    # Очистка экрана и скрытие курсора
    sys.stdout.write(ANSI.CLEAR)
    sys.stdout.write("\033[?25l")  # Скрыть курсор
    sys.stdout.flush()

    try:
        emulator.run_server(args.duration)
    except KeyboardInterrupt:
        print(f"\n{ANSI.YELLOW}Остановка по сигналу пользователя{ANSI.RESET}")
    finally:
        print("\033[?25h")  # Показать курсор
        emulator.print_stats()


if __name__ == "__main__":
    main()
