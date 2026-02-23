import binascii

# Команды управления из трафика
test_strings = [
    '536574444f2d4f4e',    # SetDO-ON
    '536574444f2d4f4646',  # SetDO-OFF
    '5363656e6172696f',    # Scenario
    '4d65766f6c757465',    # Mevoluto
    '4d636f6c6c6175646f', # Mcollaudo
    '5374616e644279',      # StandBy
    '5265676f6c4175746f', # RegolAuto
    '417a7a657261436f6e74', # AzzeraCont
    '417a7a65726154756e6e656c', # AzzeraTunnel
    '446174694163714e6f43616c', # DatiAcqNoCal
    '446967496e',          # DigIn
    '4469674f7574',        # DigOut
    '4572726f7269',        # Errori
    '537461746f4c6f6f70', # StatoLoop
]

print("=== Команды управления из трафика ===\n")
for hex_str in test_strings:
    try:
        decoded = binascii.unhexlify(hex_str).decode('ascii', errors='replace')
        print(f"{hex_str} -> {decoded}")
    except Exception as e:
        print(f"{hex_str} -> ERROR: {e}")

print("\n=== Выводы ===")
print("""
На основе анализа трафика выявлены следующие команды управления:

1. SetDO-ON / SetDO-OFF - Включение/выключение дискретных выходов (питание постов)
2. Scenario - Сценарий эксперимента
3. Mevoluto - ? (возможно, эволюция/режим)
4. Mcollaudo - ? (коллаудирование?)
5. StandBy - Режим ожидания
6. RegolAuto - Автоматическое регулирование
7. AzzeraCont - Сброс счётчика (итальянский "azzera" = обнулить)
8. AzzeraTunnel - Сброс тоннеля?
9. DatiAcqNoCal - Данные АЦП без калибровки
10. DigIn / DigOut - Цифровые входы/выходы
11. Errori - Ошибки (итальянский)
12. StatoLoop - Состояние контура

Протокол использует итальянские идентификаторы!
""")
