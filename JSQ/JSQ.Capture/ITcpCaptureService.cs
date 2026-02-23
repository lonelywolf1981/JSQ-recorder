using JSQ.Core.Models;

namespace JSQ.Capture;

/// <summary>
/// Сервис захвата TCP потока
/// </summary>
public interface ITcpCaptureService : IDisposable
{
    /// <summary>
    /// Доступные сетевые интерфейсы
    /// </summary>
    IReadOnlyList<NetworkInterfaceInfo> AvailableInterfaces { get; }
    
    /// <summary>
    /// Текущая статистика захвата
    /// </summary>
    CaptureStatistics Statistics { get; }
    
    /// <summary>
    /// Статус подключения
    /// </summary>
    ConnectionStatus Status { get; }
    
    /// <summary>
    /// Событие получения данных
    /// </summary>
    event EventHandler<byte[]>? DataReceived;
    
    /// <summary>
    /// Событие изменения статуса
    /// </summary>
    event EventHandler<ConnectionStatus>? StatusChanged;
    
    /// <summary>
    /// Получить список доступных интерфейсов
    /// </summary>
    Task<IReadOnlyList<NetworkInterfaceInfo>> GetInterfacesAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Подключиться к передатчику
    /// </summary>
    Task ConnectAsync(string host, int port, CancellationToken ct = default);
    
    /// <summary>
    /// Отключиться от передатчика
    /// </summary>
    Task DisconnectAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Отправить данные передатчику
    /// </summary>
    Task SendAsync(byte[] data, CancellationToken ct = default);
}
