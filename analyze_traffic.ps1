# Script to analyze pcapng traffic
$pcapPath = "C:\Users\a.baidenko\Downloads\JSQ-recording\_from_etl_test.pcapng"
$tsharkPath = "C:\Program Files\Wireshark\tshark.exe"

Write-Host "=== Анализ трафика JSQ ===" -ForegroundColor Cyan
Write-Host ""

# Пакеты от передатчика (источник данных)
Write-Host "1. Пакеты ОТ передатчика (192.168.0.214 -> приложение):" -ForegroundColor Yellow
& $tsharkPath -r $pcapPath -Y "tcp.port == 55555 && ip.src == 192.168.0.214" -T fields -e frame.number -e tcp.payload 2>$null | Select-Object -First 5

Write-Host ""
Write-Host "2. Пакеты К передатчику (приложение -> 192.168.0.214):" -ForegroundColor Yellow
& $tsharkPath -r $pcapPath -Y "tcp.port == 55555 && ip.dst == 192.168.0.214" -T fields -e frame.number -e tcp.payload 2>$null | Select-Object -First 5

Write-Host ""
Write-Host "3. Статистика по потоку:" -ForegroundColor Yellow
& $tsharkPath -r $pcapPath -Y "tcp.port == 55555" -q -z conv,tcp 2>$null | Select-Object -First 10
