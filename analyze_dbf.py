import struct

dbf_path = r"C:\Users\a.baidenko\Downloads\JSQ-recording\dbf\Prova001.dbf"

with open(dbf_path, 'rb') as f:
    # DBF III+ header (32 bytes)
    header = f.read(32)
    
    sig = header[0]
    year = header[1]
    month = header[2]
    day = header[3]
    num_rec = struct.unpack('<I', header[4:8])[0]
    header_len = struct.unpack('<H', header[8:10])[0]
    rec_len = struct.unpack('<H', header[10:12])[0]
    
    print("=== DBF Header ===")
    print(f"Signature: 0x{sig:02X} (DBF III+)")
    print(f"Date: {year:02d}/{month:02d}/{day:02d}")
    print(f"Number of records: {num_rec:,}")
    print(f"Header length: {header_len} bytes")
    print(f"Record length: {rec_len} bytes")
    print(f"Data start offset: {header_len}")
    print(f"File size estimate: {header_len + num_rec * rec_len:,} bytes")
    
    # Field descriptors (32 bytes each, terminated by 0x0D)
    print("\n=== Fields ===")
    fields = []
    pos = 32
    while True:
        f.seek(pos)
        field_desc = f.read(32)
        if field_desc[0] == 0x0D:
            break
        field_name = field_desc[:11].rstrip(b'\x00').decode('ascii', errors='replace')
        field_type = chr(field_desc[11])
        field_addr = struct.unpack('<I', field_desc[12:16])[0]
        field_len = field_desc[16]
        field_dec = field_desc[17]
        fields.append((field_name, field_type, field_len, field_dec, field_addr))
        pos += 32
    
    for name, ftype, flen, fdec, faddr in fields:
        dec_str = f".{fdec}" if fdec else ""
        print(f"  {name:12} | Type: {ftype} | Len: {flen:3}{dec_str} | Addr: {faddr}")
    
    print(f"\n=== Sample Data (first 3 records) ===")
    for i in range(3):
        f.seek(header_len + i * rec_len)
        record = f.read(rec_len)
        # Parse as text
        text = record.decode('cp1252', errors='replace').rstrip('\x00\x1a')
        print(f"Record {i+1}: {text[:200]}...")
