using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using JSQ.Core.Models;
using JSQ.Capture.Pipeline;

namespace JSQ.Capture;

/// <summary>
/// Сервис захвата TCP потока от передатчика
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
    
    // Очереди pipeline
    private readonly IngestQueue _ingestQueue = new(10000);
    private readonly DecodeQueue _decodeQueue = new(5000);
    
    // Конфигурация
    private string _host = "192.168.0.214";
    private int _port = 55555;
    private int _connectionTimeoutMs = 5000;
    private int _readTimeoutMs = 1000;

    public int ConnectionTimeoutMs { get => _connectionTimeoutMs; set => _connectionTimeoutMs = value; }
    
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
        LoadInterfaces();
    }
    
    private void LoadInterfaces()
    {
        var interfaces = new List<NetworkInterfaceInfo>();
        
        try
        {
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
        }
        catch
        {
            // Если не удалось получить интерфейсы
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
        
        _host = host;
        _port = port;
        Status = ConnectionStatus.Connecting;
        
        try
        {
            _client = new TcpClient();
            _cts = new CancellationTokenSource();
            
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            
            // Подключение с таймаутом
            var connectTask = _client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(_connectionTimeoutMs, linkedCts.Token);
            
            if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
            {
                throw new TimeoutException($"Не удалось подключиться к {host}:{port} за {_connectionTimeoutMs} мс");
            }
            
            await connectTask;
            
            _stream = _client.GetStream();
            _stream.ReadTimeout = _readTimeoutMs;
            _stream.WriteTimeout = _readTimeoutMs;
            
            Status = ConnectionStatus.Connected;
            
            // Запускаем прием данных
            _receiveTask = ReceiveLoopAsync(_cts.Token);
            
            // Логируем событие
            DataReceived?.Invoke(this, System.Text.Encoding.UTF8.GetBytes(
                $"[INFO] Подключено к {host}:{port}"));
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
                await Task.Run(() =>
                {
                    try { _receiveTask?.Wait(TimeSpan.FromSeconds(2)); }
                    catch (AggregateException) { }
                }, ct);
            }
            catch (OperationCanceledException) { }
        }
        
        _stream?.Dispose();
        _client?.Dispose();
        _cts?.Dispose();
        
        _stream = null;
        _client = null;
        _cts = null;
        
        DataReceived?.Invoke(this, System.Text.Encoding.UTF8.GetBytes(
            $"[INFO] Отключено"));
    }
    
    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (_stream == null || Status != ConnectionStatus.Connected)
            throw new InvalidOperationException("Не подключено");
        
        await Task.Run(() =>
        {
            _stream!.Write(data, 0, data.Length);
            _stream.Flush();
        }, ct);
    }
    
    public async Task SendCommandAsync(string command, CancellationToken ct = default)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(command + "\r\n");
        await SendAsync(data, ct);
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
                        catch (IOException) when (ct.IsCancellationRequested)
                        {
                            return 0;
                        }
                        catch (IOException)
                        {
                            // Таймаут чтения — не разрыв соединения, продолжаем ожидание
                            return -1;
                        }
                    }, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (bytesRead == -1)
                    continue; // Таймаут чтения — пробуем снова

                if (bytesRead == 0)
                {
                    // Удалённое закрытие соединения
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
                
                // Отправляем в pipeline
                _ingestQueue.Enqueue(data);
                
                // Уведомляем подписчиков
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
    
    /// <summary>
    /// Получить данные из ingest очереди
    /// </summary>
    public bool TryGetIngestData(out PipelineItem<byte[]> item)
    {
        return _ingestQueue.TryDequeue(out item);
    }
    
    /// <summary>
    /// Добавить данные в decode очередь
    /// </summary>
    public void AddToDecodeQueue(TcpSegment segment)
    {
        _decodeQueue.Enqueue(segment);
    }
    
    /// <summary>
    /// Получить данные из decode очереди
    /// </summary>
    public bool TryGetDecodedData(out PipelineItem<TcpSegment> item)
    {
        return _decodeQueue.TryDequeue(out item);
    }
    
    /// <summary>
    /// Статистика очередей
    /// </summary>
    public (int ingest, int decode, int persist) GetQueueSizes()
    {
        return (_ingestQueue.Count, _decodeQueue.Count, 0);
    }
    
    public void Dispose()
    {
        Task.Run(() => DisconnectAsync(CancellationToken.None)).Wait(TimeSpan.FromSeconds(2));
        GC.SuppressFinalize(this);
    }
}
