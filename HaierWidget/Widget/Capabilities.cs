using System.Text.Json.Nodes;
using HaierWidget.Haier;

namespace HaierWidget.Widget;

/// <summary>控件类型。</summary>
public enum Kind
{
    None,
    Power,
    Temperature,
    Mode,
    Fan,
    Switch,
    Select,
    Number,
    Button,
    Sensor,
}

public sealed class Control
{
    public required string Key { get; init; }
    public required string Label { get; set; }
    public Kind Kind { get; set; }
    public List<(string Value, string Label)> Options { get; set; } = new();
    public double MinValue { get; set; }
    public double MaxValue { get; set; }
    public double Step { get; set; } = 1;
    public string OnValue { get; set; } = "true";
    public string OffValue { get; set; } = "false";
    public string Unit { get; set; } = "";
    public int Order { get; set; } = 100;
    public bool Advanced { get; set; }
    public string Desc { get; set; } = "";

    public string OptionLabel(string? value)
    {
        var s = value ?? "";
        foreach (var (v, label) in Options)
        {
            if (v == s)
            {
                return label;
            }
        }
        return s;
    }

    public bool IsOn(string? value) => value == OnValue;

    public string Format(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "--";
        }
        if (Options.Count > 0)
        {
            return OptionLabel(value);
        }
        return Unit.Length > 0 ? value + Unit : value;
    }
}

/// <summary>把设备数字模型 attributes 解析为控件列表，并生成状态摘要。</summary>
public static class Capabilities
{
    private static readonly HashSet<string> HiddenKeys = new() { "getAllProperty", "getAllAlarm" };

    /// <summary>OffLabel / OnLabel：两项开关的选项短名（云端 desc 太长，如“左右摆位置八 (自动)”）。</summary>
    private sealed record Known(string Label, Kind Kind = Kind.None, string Unit = "", int Order = 100, bool Advanced = false,
        string? OffLabel = null, string? OnLabel = null);

    private static readonly Dictionary<string, Known> KnownMap = new()
    {
        ["onOffStatus"] = new("电源", Kind.Power, Order: 0),
        ["targetTemperature"] = new("目标温度", Kind.Temperature, "°C", 1),
        ["targetTemp"] = new("目标温度", Kind.Temperature, "°C", 1),
        ["operationMode"] = new("模式", Kind.Mode, Order: 2),
        ["windSpeed"] = new("风速", Kind.Fan, Order: 3),
        ["windSpeedL"] = new("左风速", Kind.Fan, Order: 3),
        ["windSpeedR"] = new("右风速", Kind.Fan, Order: 4),
        ["indoorTemperature"] = new("室温", Kind.Sensor, "°C", 0),
        ["indoorHumidity"] = new("室内湿度", Kind.Sensor, "%", 1),
        ["outdoorTemperature"] = new("室外温度", Kind.Sensor, "°C", 2),
        ["healthMode"] = new("负离子", Order: 10),
        ["freshAirStatus"] = new("新风", Order: 11),
        ["selfCleaningStatus"] = new("自清洁", Order: 12),
        ["electricHeatingStatus"] = new("电辅热", Order: 13),
        ["humidificationStatus"] = new("加湿", Order: 14),
        ["windDirectionVertical"] = new("上下摆风", Order: 15, OffLabel: "固定", OnLabel: "自动"),
        ["windDirectionHorizontal"] = new("左右摆风", Order: 16, OffLabel: "固定", OnLabel: "自动"),
        ["sleepCurveStatus"] = new("睡眠模式", Order: 17),
        ["lockStatus"] = new("童锁", Order: 18),
        ["echoStatus"] = new("静音提示音", Order: 19),
        ["humanSensingStatus"] = new("人感", Order: 20, OffLabel: "关", OnLabel: "开"),
        ["targetHumidity"] = new("目标湿度", Unit: "%", Order: 30, Advanced: true),
        ["stopCurrentAlarm"] = new("停止报警", Kind.Button, Order: 90, Advanced: true),
    };

    private static readonly Dictionary<string, string> ModeAliases = new()
    {
        ["智能/自动/舒适"] = "自动",
        ["智能"] = "自动",
    };

    public static string SimplifyDesc(string? desc)
    {
        desc = (desc ?? "").Trim();
        if (ModeAliases.TryGetValue(desc, out var alias))
        {
            return alias;
        }
        if (desc.Contains('/') && desc.Length > 6)
        {
            var parts = desc.Split('/').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            foreach (var p in parts)
            {
                if (p.Contains("自动"))
                {
                    return "自动";
                }
            }
            if (parts.Count > 0)
            {
                return parts[0];
            }
        }
        return desc;
    }

    private static List<(string, string)> ListItems(JsonObject attr)
    {
        var items = new List<(string, string)>();
        if (attr["valueRange"]?["dataList"] is JsonArray list)
        {
            foreach (var i in list)
            {
                if (i is not JsonObject o)
                {
                    continue;
                }
                var data = Json.Str(o["data"]) ?? "";
                var desc = Json.Str(o["desc"]);
                items.Add((data, SimplifyDesc(string.IsNullOrEmpty(desc) || desc == "null" ? data : desc)));
            }
        }
        return items;
    }

    public static Control? ParseAttribute(JsonObject attr)
    {
        var name = Json.Str(attr["name"]);
        if (string.IsNullOrEmpty(name) || HiddenKeys.Contains(name))
        {
            return null;
        }
        KnownMap.TryGetValue(name, out var known);
        bool writable = Json.Bool(attr["writable"]);
        bool readable = attr["readable"] is null || Json.Bool(attr["readable"]);
        var rtype = (Json.Str(attr["valueRange"]?["type"]) ?? "").ToUpperInvariant();
        var desc = Json.Str(attr["desc"]) is { Length: > 0 } d ? d : name;

        var ctrl = new Control
        {
            Key = name,
            Label = known?.Label ?? desc,
            Kind = known?.Kind ?? Kind.None,
            Unit = known?.Unit ?? "",
            Order = known?.Order ?? 100,
            Advanced = known?.Advanced ?? false,
            Desc = desc,
        };

        if (!writable)
        {
            if (!readable || !attr.ContainsKey("value"))
            {
                return null;
            }
            ctrl.Kind = Kind.Sensor;
            if (rtype == "LIST")
            {
                ctrl.Options = ListItems(attr);
            }
            return ctrl;
        }

        if (rtype == "STEP")
        {
            var step = attr["valueRange"]?["dataStep"] as JsonObject;
            var min = Json.Double(step?["minValue"]);
            var max = Json.Double(step?["maxValue"]);
            var st = Json.Double(step?["step"]);
            if (min is null || max is null)
            {
                return null;
            }
            ctrl.MinValue = min.Value;
            ctrl.MaxValue = max.Value;
            ctrl.Step = st is > 0 ? st.Value : 1;
            if (ctrl.Kind != Kind.Temperature)
            {
                ctrl.Kind = Kind.Number;
            }
            return ctrl;
        }

        if (rtype == "LIST")
        {
            var items = ListItems(attr);
            if (ctrl.Kind is Kind.Mode or Kind.Fan && items.All(i => long.TryParse(i.Item1, out _)))
            {
                items = items.OrderBy(i => long.Parse(i.Item1)).ToList();
            }
            ctrl.Options = items;
            var values = items.Select(i => i.Item1).ToHashSet();
            if (ctrl.Kind == Kind.Power)
            {
                ctrl.OnValue = "true";
                ctrl.OffValue = "false";
                return ctrl;
            }
            if (ctrl.Kind is Kind.Mode or Kind.Fan or Kind.Select)
            {
                return ctrl;
            }
            if (ctrl.Kind == Kind.Button || items.Count == 1)
            {
                ctrl.Kind = Kind.Button;
                return ctrl;
            }
            if (items.Count == 2)
            {
                ctrl.Kind = Kind.Switch;
                if (values.SetEquals(new[] { "true", "false" }))
                {
                    ctrl.OnValue = "true";
                    ctrl.OffValue = "false";
                }
                else
                {
                    ctrl.OffValue = items[0].Item1;
                    ctrl.OnValue = items[1].Item1;
                }
                if (known?.OffLabel is not null && known.OnLabel is not null)
                {
                    ctrl.Options = new List<(string, string)> { (ctrl.OffValue, known.OffLabel), (ctrl.OnValue, known.OnLabel) };
                }
                return ctrl;
            }
            ctrl.Kind = Kind.Select;
            return ctrl;
        }

        return null;
    }

    public static List<Control> BuildControls(JsonArray attributes)
    {
        var controls = new List<Control>();
        foreach (var a in attributes)
        {
            if (a is not JsonObject o)
            {
                continue;
            }
            Control? ctrl = null;
            try
            {
                ctrl = ParseAttribute(o);
            }
            catch
            {
                ctrl = null;
            }
            if (ctrl is not null)
            {
                controls.Add(ctrl);
            }
        }
        return controls
            .OrderBy(c => c.Advanced)
            .ThenBy(c => c.Order)
            .ThenBy(c => c.Label, StringComparer.Ordinal)
            .ToList();
    }

    public static Control? Find(IEnumerable<Control> controls, Kind kind) => controls.FirstOrDefault(c => c.Kind == kind);

    public static Control? FindKey(IEnumerable<Control> controls, string key) => controls.FirstOrDefault(c => c.Key == key);

    /// <summary>一行状态摘要，例如 "制冷 · 24°C · 风速自动 · 室温 25°C"。</summary>
    public static string Summary(IReadOnlyList<Control> controls, IReadOnlyDictionary<string, string> values, bool online = true)
    {
        if (!online)
        {
            return "离线";
        }
        if (values.Count == 0)
        {
            return "等待数据…";
        }
        var parts = new List<string>();
        var power = Find(controls, Kind.Power);
        if (power is not null && values.TryGetValue(power.Key, out var pv) && !power.IsOn(pv))
        {
            parts.Add("已关机");
        }
        else
        {
            var mode = Find(controls, Kind.Mode);
            if (mode is not null && values.TryGetValue(mode.Key, out var mv))
            {
                parts.Add(mode.OptionLabel(mv));
            }
            var temp = Find(controls, Kind.Temperature);
            if (temp is not null && values.TryGetValue(temp.Key, out var tv))
            {
                parts.Add(temp.Format(tv));
            }
            var fan = Find(controls, Kind.Fan);
            if (fan is not null && values.TryGetValue(fan.Key, out var fv))
            {
                parts.Add("风速" + fan.OptionLabel(fv));
            }
        }
        foreach (var key in new[] { "indoorTemperature", "indoorHumidity" })
        {
            var c = FindKey(controls, key);
            if (c is not null && values.TryGetValue(key, out var v))
            {
                parts.Add($"{c.Label} {c.Format(v)}");
            }
        }
        if (parts.Count == 0)
        {
            foreach (var c in controls)
            {
                if (values.TryGetValue(c.Key, out var v) && c.Kind != Kind.Button)
                {
                    parts.Add($"{c.Label} {c.Format(v)}");
                }
                if (parts.Count >= 3)
                {
                    break;
                }
            }
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : "在线";
    }

    /// <summary>
    /// 合并两份属性定义：按 name 去重，后者覆盖前者（用于按型号缓存）。
    /// writable 例外，取“曾经可写”的并集：云端在关机时会把目标温度 / 模式 / 风速标成只读，
    /// 若直接覆盖，这些控件会随关机从卡片上消失，开机后还得等下一次 HTTP 刷新才回来。
    /// 关机时能不能点由 DeviceCard 按电源状态决定，与定义无关。
    /// </summary>
    public static JsonArray MergeAttributes(JsonArray? cached, JsonArray fresh)
    {
        var byName = new Dictionary<string, JsonObject>();
        var order = new List<string>();
        void Add(JsonArray arr, bool keepWritable)
        {
            foreach (var a in arr)
            {
                if (a is JsonObject o && Json.Str(o["name"]) is { Length: > 0 } n)
                {
                    var clone = (JsonObject)o.DeepClone();
                    if (keepWritable && byName.TryGetValue(n, out var prev) && Json.Bool(prev["writable"]))
                    {
                        clone["writable"] = true;
                    }
                    if (!byName.ContainsKey(n))
                    {
                        order.Add(n);
                    }
                    byName[n] = clone;
                }
            }
        }
        if (cached is not null)
        {
            Add(cached, keepWritable: false);
        }
        Add(fresh, keepWritable: true);
        var result = new JsonArray();
        foreach (var n in order)
        {
            result.Add(byName[n]);
        }
        return result;
    }
}
