using System.Net.Sockets;
using System.Collections.Concurrent;
using JSQ.Core.Models;

namespace JSQ.Capture;

/// <summary>
/// Сервис захвата TCP потока
/// </summary>
public class TcpCaptureService : ITcpCaptureService
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private readonly object _lock = new();
    
    // Статистика
    private readonly CaptureStatistics _stats = new();
    private ulong _lastBytesCount;
    private DateTime _lastStatsTime = DateTime.MinValue;
    
    public IReadOnlyList<NetworkInterfaceInfo> AvailableInterfaces { get; private set; } 
        = new List<NetworkInterfaceInfo>();
    
    public CaptureStatistics Statistics => _stats;
    
    public ConnectionStatus Status
    {
        get => _stats.Status;
        private set
        {
            if (_stats.Status != value)
            {
                _stats.Status = value;
                StatusChanged?.Invoke(this, value);
            }
        }
    }
    
    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<ConnectionStatus>? StatusChanged;
    
    public TcpCaptureService()
    {
        // Загружаем доступные интерфейсы при старте
        LoadInterfaces();
    }
    
    private void LoadInterfaces()
    {
        var interfaces = new List<NetworkInterfaceInfo>();
        
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                continue;
                
            var ipProps = ni.GetIPProperties();
            var ip = ipProps.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?
                .Address?.ToString() ?? string.Empty;
            
            interfaces.Add(new NetworkInterfaceInfo
            {
                Id = ni.Id,
                Name = ni.Name,
                Description = ni.Description,
                IpAddress = ip,
                IsUp = ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up,
                SupportsMulticast = ni.SupportsMulticast
            });
        }
        
        AvailableInterfaces = interfaces.AsReadOnly();
    }
    
    public Task<IReadOnlyList<NetworkInterfaceInfo>> GetInterfacesAsync(CancellationToken ct = default)
    {
        return Task.FromResult(AvailableInterfaces);
    }
    
    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        if (Status == ConnectionStatus.Connected)
            return;
        
        Status = ConnectionStatus.Connecting;
        
        try
        {
            _client = new TcpClient();
            _cts = new CancellationTokenSource();
            
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            
            // Подключение с таймаутом
            var connectTask = _client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(5000, linkedCts.Token);
            
            if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
            {
                throw new TimeoutException($"Не удалось подключиться к {host}:{port} за 5 секунд");
            }
            
            await connectTask; // Проброс исключения если есть
            
            _stream = _client.GetStream();
            _stream.ReadTimeout = 1000;
            
            Status = ConnectionStatus.Connected;
            
            // Запускаем прием данных
            _receiveTask = ReceiveLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Status = ConnectionStatus.Error;
            _stats.ErrorMessage = ex.Message;
            throw;
        }
    }
    
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        Status = ConnectionStatus.Disconnected;

        _cts?.Cancel();

        if (_receiveTask != null)
        {
            try
            {
                // Для .NET Framework 4.8 используем Task.Wait с таймаутом
                await Task.Run(() => _receiveTask.Wait(TimeSpan.FromSeconds(2)), ct);
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }

        _stream?.Dispose();
        _client?.Dispose();
        _cts?.Dispose();

        _stream = null;
        _client = null;
        _cts = null;
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_stream == null || _client?.Connected != true)
            throw new InvalidOperationException("Не подключено");

        await Task.Run(() =>
        {
            _stream!.Write(data, 0, data.Length);
            _stream.Flush();
        }, ct);
    }
    
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];

        try
        {
            while (!ct.IsCancellationRequested && _client?.Connected == true)
            {
                if (_stream == null)
                    break;

                int bytesRead;
                try
                {
                    // Для .NET Framework 4.8 используем синхронный Read в Task.Run
                    bytesRead = await Task.Run(() =>
                    {
                        try
                        {
                            return _stream!.Read(buffer, 0, buffer.Length);
                        }
                        catch (IOException)
                        {
                            if (ct.IsCancellationRequested)
                                return 0;
                            throw;
                        }
                    }, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (bytesRead == 0)
                {
                    // Удаленное закрытие соединения
                    Status = ConnectionStatus.Disconnected;
                    break;
                }

                // Обновляем статистику
                lock (_lock)
                {
                    _stats.TotalBytesReceived += (ulong)bytesRead;
                    _stats.TotalPacketsReceived++;
                    _stats.LastPacketTime = DateTime.Now;

                    // Расчет скорости
                    var now = DateTime.Now;
                    if ((now - _lastStatsTime).TotalSeconds >= 1)
                    {
                        _stats.BytesPerSecond = _stats.TotalBytesReceived - _lastBytesCount;
                        _lastBytesCount = _stats.TotalBytesReceived;
                        _lastStatsTime = now;
                    }
                }

                // Копируем данные для подписчиков
                var data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);
                DataReceived?.Invoke(this, data);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = ConnectionStatus.Error;
            _stats.ErrorMessage = ex.Message;
        }
    }
    
    public void Dispose()
    {
        DisconnectAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
    }
}
