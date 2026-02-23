using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace JSQ.Core.Models;

/// <summary>
/// Канонический реестр всех каналов передатчика.
/// Ключ — сырой протокольный индекс (v000..v149).
/// </summary>
public static class ChannelRegistry
{
    private static readonly Dictionary<int, ChannelDefinition> _byIndex;

    static ChannelRegistry()
    {
        _byIndex = new Dictionary<int, ChannelDefinition>();

        // --- Давление общее (посты A/B/C, индексы 0-5) ---
        Add(0,  "v000", "A-Pc",   "bara", "unit A - Discharge Pressure",   ChannelGroup.PostA, ChannelType.Pressure);
        Add(1,  "v001", "A-Pe",   "bara", "unit A - Suction Pressure",     ChannelGroup.PostA, ChannelType.Pressure);
        Add(2,  "v002", "B-Pc",   "bara", "unit B - Discharge Pressure",   ChannelGroup.PostB, ChannelType.Pressure);
        Add(3,  "v003", "B-Pe",   "bara", "unit B - Suction Pressure",     ChannelGroup.PostB, ChannelType.Pressure);
        Add(4,  "v004", "C-Pc",   "bara", "unit C - Discharge Pressure",   ChannelGroup.PostC, ChannelType.Pressure);
        Add(5,  "v005", "C-Pe",   "bara", "unit C - Suction Pressure",     ChannelGroup.PostC, ChannelType.Pressure);

        // --- Общие каналы (индексы 6-15) ---
        Add(6,  "v006", "VEL",    "m/s",  "Velocity",                      ChannelGroup.Common, ChannelType.Flow);
        Add(7,  "v007", "UR",     "%",    "Relative Humidity",             ChannelGroup.Common, ChannelType.Humidity);
        Add(8,  "v008", "mA1",    "mA",   "Current loop 1",               ChannelGroup.Common, ChannelType.CurrentLoop);
        Add(9,  "v009", "mA2",    "mA",   "Current loop 2",               ChannelGroup.Common, ChannelType.CurrentLoop);
        Add(10, "v010", "mA3",    "mA",   "Current loop 3",               ChannelGroup.Common, ChannelType.CurrentLoop);
        Add(11, "v011", "mA4",    "mA",   "Current loop 4",               ChannelGroup.Common, ChannelType.CurrentLoop);
        Add(12, "v012", "mA5",    "mA",   "Current loop 5",               ChannelGroup.Common, ChannelType.CurrentLoop);
        Add(13, "v013", "Flux",   "l/m",  "Flow rate",                    ChannelGroup.Common, ChannelType.Flow);
        Add(14, "v014", "UR-sie", "%",    "Humidity Siemens",             ChannelGroup.Common, ChannelType.Humidity);
        Add(15, "v015", "T-sie",  "°C",   "Temperature Siemens",          ChannelGroup.Common, ChannelType.Temperature);

        // --- Пост A: температуры (индексы 16-47) ---
        Add(16, "v016", "A-Tc", "°C", "unit A - Condensing temperature",  ChannelGroup.PostA, ChannelType.Temperature);
        Add(17, "v017", "A-Te", "°C", "unit A - Evaporation temperature", ChannelGroup.PostA, ChannelType.Temperature);
        for (int i = 1; i <= 30; i++)
            Add(17 + i, $"v{17 + i:D3}", $"A-T{i}", "°C", $"unit A - Temperature {i}", ChannelGroup.PostA, ChannelType.Temperature);

        // --- Пост A: электрические (индексы 48-53) ---
        Add(48, "v048", "A-I",    "A",   "unit A - Current",      ChannelGroup.PostA, ChannelType.Electrical);
        Add(49, "v049", "A-F",    "Hz",  "unit A - Frequency",    ChannelGroup.PostA, ChannelType.Electrical);
        Add(50, "v050", "A-V",    "V",   "unit A - Voltage",      ChannelGroup.PostA, ChannelType.Electrical);
        Add(51, "v051", "A-W",    "W",   "unit A - Power",        ChannelGroup.PostA, ChannelType.Electrical);
        Add(52, "v052", "A-PF",   "",    "unit A - Power Factor", ChannelGroup.PostA, ChannelType.Electrical);
        Add(53, "v053", "A-MaxI", "A",   "unit A - Max Current",  ChannelGroup.PostA, ChannelType.Electrical);

        // --- Пост B: температуры (индексы 54-85) ---
        Add(54, "v054", "B-Tc", "°C", "unit B - Condensing temperature",  ChannelGroup.PostB, ChannelType.Temperature);
        Add(55, "v055", "B-Te", "°C", "unit B - Evaporation temperature", ChannelGroup.PostB, ChannelType.Temperature);
        for (int i = 1; i <= 30; i++)
            Add(55 + i, $"v{55 + i:D3}", $"B-T{i}", "°C", $"unit B - Temperature {i}", ChannelGroup.PostB, ChannelType.Temperature);

        // --- Пост B: электрические (индексы 86-91) ---
        Add(86, "v086", "B-I",    "A",   "unit B - Current",      ChannelGroup.PostB, ChannelType.Electrical);
        Add(87, "v087", "B-F",    "Hz",  "unit B - Frequency",    ChannelGroup.PostB, ChannelType.Electrical);
        Add(88, "v088", "B-V",    "V",   "unit B - Voltage",      ChannelGroup.PostB, ChannelType.Electrical);
        Add(89, "v089", "B-W",    "W",   "unit B - Power",        ChannelGroup.PostB, ChannelType.Electrical);
        Add(90, "v090", "B-PF",   "",    "unit B - Power Factor", ChannelGroup.PostB, ChannelType.Electrical);
        Add(91, "v091", "B-MaxI", "A",   "unit B - Max Current",  ChannelGroup.PostB, ChannelType.Electrical);

        // --- Пост C: температуры (индексы 100-131) ---
        Add(100, "v100", "C-Tc", "°C", "unit C - Condensing temperature",  ChannelGroup.PostC, ChannelType.Temperature);
        Add(101, "v101", "C-Te", "°C", "unit C - Evaporation temperature", ChannelGroup.PostC, ChannelType.Temperature);
        for (int i = 1; i <= 30; i++)
            Add(101 + i, $"v{101 + i:D3}", $"C-T{i}", "°C", $"unit C - Temperature {i}", ChannelGroup.PostC, ChannelType.Temperature);

        // --- Пост C: электрические (индексы 132-137) ---
        Add(132, "v132", "C-I",    "A",   "unit C - Current",      ChannelGroup.PostC, ChannelType.Electrical);
        Add(133, "v133", "C-F",    "Hz",  "unit C - Frequency",    ChannelGroup.PostC, ChannelType.Electrical);
        Add(134, "v134", "C-V",    "V",   "unit C - Voltage",      ChannelGroup.PostC, ChannelType.Electrical);
        Add(135, "v135", "C-W",    "W",   "unit C - Power",        ChannelGroup.PostC, ChannelType.Electrical);
        Add(136, "v136", "C-PF",   "",    "unit C - Power Factor", ChannelGroup.PostC, ChannelType.Electrical);
        Add(137, "v137", "C-MaxI", "A",   "unit C - Max Current",  ChannelGroup.PostC, ChannelType.Electrical);

        // --- Системные (индексы 146-149, из CHANNEL_SPEC.md) ---
        Add(146, "v146", "SYS-1", "", "System channel 1", ChannelGroup.System, ChannelType.System);
        Add(147, "v147", "SYS-2", "", "System channel 2", ChannelGroup.System, ChannelType.System);
        Add(148, "v148", "SYS-3", "", "System channel 3", ChannelGroup.System, ChannelType.System);
        Add(149, "v149", "SYS-4", "", "System channel 4", ChannelGroup.System, ChannelType.System);
    }

    public static IReadOnlyDictionary<int, ChannelDefinition> All =>
        new ReadOnlyDictionary<int, ChannelDefinition>(_byIndex);

    /// <summary>Найти канал по протокольному индексу. Возвращает null если не найден.</summary>
    public static ChannelDefinition? GetByIndex(int index) =>
        _byIndex.TryGetValue(index, out var ch) ? ch : null;

    /// <summary>Отображаемое имя канала по индексу, с fallback на "v{index}".</summary>
    public static string GetName(int index) =>
        _byIndex.TryGetValue(index, out var ch) ? ch.Name : $"v{index:D3}";

    /// <summary>Единица измерения по индексу.</summary>
    public static string GetUnit(int index) =>
        _byIndex.TryGetValue(index, out var ch) ? ch.Unit : string.Empty;

    private static void Add(int index, string raw, string name, string unit, string desc,
                            ChannelGroup group, ChannelType type)
    {
        _byIndex[index] = new ChannelDefinition
        {
            Index = index,
            RawCode = raw,
            Name = name,
            Unit = unit,
            Description = desc,
            Group = group,
            Type = type,
            Enabled = true
        };
    }
}
