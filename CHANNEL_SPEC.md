# Спецификация каналов JSQ (134 канала)

## Источник
Файл: `out.xlsx`, лист `Mapping`

## Канонический список каналов

| idx | raw  | name | unit | description |
|-----|------|------|------|-------------|
| 0   | v000 | A-Pc | bara | unit A - Discharge Pressure |
| 1   | v001 | A-Pe | bara | unit A - Suction Pressure |
| 2   | v002 | B-Pc | bara | unit B - Discharge Pressure |
| 3   | v003 | B-Pe | bara | unit B - Suction Pressure |
| 4   | v004 | C-Pc | bara | unit C - Discharge Pressure |
| 5   | v005 | C-Pe | bara | unit C - Suction Pressure |
| 6   | v006 | VEL  | m/s  | Velocity |
| 7   | v007 | UR   | %    | Relative Humidity |
| 8   | v008 | mA1  | mA   | Current loop 1 |
| 9   | v009 | mA2  | mA   | Current loop 2 |
| 10  | v010 | mA3  | mA   | Current loop 3 |
| 11  | v011 | mA4  | mA   | Current loop 4 |
| 12  | v012 | mA5  | mA   | Current loop 5 |
| 13  | v013 | Flux | l/m  | Flow rate |
| 14  | v014 | UR-sie | %  | Humidity Siemens |
| 15  | v015 | T-sie | °C | Temperature Siemens |
| 16  | v016 | A-Tc | °C | unit A - Condensing temperature |
| 17  | v017 | A-Te | °C | unit A - Evaporation temperature |
| 18  | v018 | A-T1 | °C | unit A - Temperature 1 |
| ... | ...  | ...  | ...  | ... |
| 47  | v047 | A-T30 | °C | unit A - Temperature 30 |
| 48  | v048 | A-I  | A   | unit A - Current |
| 49  | v049 | A-F  | Hz  | unit A - Frequency |
| 50  | v050 | A-V  | V   | unit A - Voltage |
| 51  | v051 | A-W  | W   | unit A - Power |
| 52  | v052 | A-PF |     | unit A - Power Factor |
| 53  | v053 | A-MaxI | A | unit A - Max Current |
| 54  | v054 | B-Tc | °C | unit B - Condensing temperature |
| ... | ...  | ...  | ...  | ... |
| 99  | v099 | B-MaxI | A | unit B - Max Current |
| 100 | v100 | C-Tc | °C | unit C - Condensing temperature |
| ... | ...  | ...  | ...  | ... |
| 145 | v145 | C-MaxI | A | unit C - Max Current |
| 146 | v146 | SYS-1 |    | System channel 1 |
| 147 | v147 | SYS-2 |    | System channel 2 |
| 148 | v148 | SYS-3 |    | System channel 3 |
| 149 | v149 | SYS-4 |    | System channel 4 |

**Примечание:** Полная таблица из 134 каналов доступна в `out.xlsx`.

## Группы каналов

### Пост A (индексы 0, 16-53)
- Давление: A-Pc, A-Pe
- Температуры: A-Tc, A-Te, A-T1..A-T30 (32 канала)
- Электрические: A-I, A-F, A-V, A-W, A-PF, A-MaxI

### Пост B (индексы 2, 3, 54-99)
- Давление: B-Pc, B-Pe
- Температуры: B-Tc, B-Te, B-T1..B-T30 (32 канала)
- Электрические: B-I, B-F, B-V, B-W, B-PF, B-MaxI

### Пост C (индексы 4, 5, 100-145)
- Давление: C-Pc, C-Pe
- Температуры: C-Tc, C-Te, C-T1..C-T30 (32 канала)
- Электрические: C-I, C-F, C-V, C-W, C-PF, C-MaxI

### Общие каналы (индексы 6-15)
- VEL (скорость)
- UR (влажность)
- mA1..mA5 (токовые петли)
- Flux (поток)
- UR-sie, T-sie (Siemens датчики)

### Системные (индексы 146-149)
- SYS-1..SYS-4

## Спецификация DBF экспорта

### Формат записи (405 байт)
```
Data       D  8    YYYYMMDD
Ore        N  2    Часы
Minuti     N  2    Минуты
Secondi    N  2    Секунды
mSecondi   N  3    Миллисекунды
[51 канал] N  8.3  Значения (или N 9.2, N 9.4 для некоторых)
```

### Частота дискретизации
- **~2 Гц** (510 записей за ~4 минуты)
- **200ms** между отсчётами

### Специальные значения
- **-99** или **-25.001** — нет данных / ошибка датчика

## Единицы измерения

| Код | Единица | Описание | Диапазон (тип.) |
|-----|---------|----------|-----------------|
| bara | bar (абс.) | Давление абсолютное | 0..30 |
| °C   | градус Цельсия | Температура | -50..150 |
| A    | Ампер | Ток | 0..100 |
| Hz   | Герц | Частота | 45..65 |
| V    | Вольт | Напряжение | 0..400 |
| W    | Ватт | Мощность | 0..10000 |
| PF   | безразмерная | Коэфф. мощности | 0..1 |
| %    | процент | Влажность | 0..100 |
| m/s  | м/с | Скорость | 0..20 |
| l/m  | л/мин | Поток | 0..1000 |
| mA   | миллиампер | Ток 4-20mA | 0..25 |

## Mapping для приложения

```csharp
public class ChannelDefinition
{
    public int Index { get; set; }      // 0-133
    public string RawCode { get; set; } // v000..v133
    public string Name { get; set; }    // A-Pc, A-T1, etc.
    public string Unit { get; set; }    // bara, °C, A, etc.
    public string Description { get; set; }
    public ChannelGroup Group { get; set; } // A, B, C, Common, System
    public ChannelType Type { get; set; }   // Pressure, Temperature, Electrical, etc.
}
```

## Следующие шаги

1. ✅ Канонический список 134 каналов зафиксирован
2. ✅ Формат DBF определён
3. ✅ Частота дискретизации: ~2 Гц
4. ⏳ Требуется: точный бинарный формат протокола (из pcapng)
