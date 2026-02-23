import pandas as pd
import os
import sys

xlsx_path = r"C:\Users\a.baidenko\Downloads\JSQ-recording\out.xlsx"

print("=== Анализ out.xlsx ===\n")

try:
    # Читаем все листы
    xl = pd.ExcelFile(xlsx_path)
    file_size = os.path.getsize(xlsx_path)
    print(f"Файл: {xlsx_path}")
    print(f"Размер: {file_size:,} байт")
    print(f"Листы: {xl.sheet_names}")
    print()
    
    for sheet in xl.sheet_names:
        print(f"{'='*60}")
        print(f"Лист: {sheet}")
        print(f"{'='*60}")
        
        df = pd.read_excel(xlsx_path, sheet_name=sheet)
        
        print(f"Строк: {len(df):,}")
        print(f"Столбцов: {len(df.columns)}")
        print(f"Столбцы: {list(df.columns)}")
        print()
        
        # Первые 5 строк
        print("Первые 5 строк:")
        print(df.head().to_string())
        print()
        
        # Статистика
        print("Статистика:")
        print(df.describe(include='all').to_string())
        print("\n")
        
except Exception as e:
    print(f"Ошибка: {e}")
    sys.exit(1)
