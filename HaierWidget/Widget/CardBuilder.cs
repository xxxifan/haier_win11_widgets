using System.Text.Json.Nodes;
using HaierWidget.Haier;

namespace HaierWidget.Widget;

/// <summary>
/// 生成小组件面板用的 Adaptive Card（1.5）。所有值直接写进模板，不使用数据绑定。
/// 交互全部走 Action.Execute，verb / data 由 WidgetProvider.OnActionInvoked 处理。
/// </summary>
public static class CardBuilder
{
    public const string VerbExpand = "expand";
    public const string VerbBack = "back";
    public const string VerbSet = "set";
    public const string VerbRefresh = "refresh";
    public const string VerbLogin = "login";
    public const string VerbSettings = "settings";
    public const string VerbLogout = "logout";

    public const string SizeSmall = "small";
    public const string SizeMedium = "medium";
    public const string SizeLarge = "large";

    // ---------------------------------------------------------------- 基础

    private static JsonObject Card(params JsonNode?[] body)
    {
        var arr = new JsonArray();
        foreach (var n in body)
        {
            if (n is not null)
            {
                arr.Add(n);
            }
        }
        return CardFrom(arr);
    }

    private static JsonObject CardFrom(JsonArray arr)
    {
        return new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.5",
            ["body"] = arr,
        };
    }

    private static JsonObject Text(string text, string? size = null, string? weight = null, string? color = null,
        bool subtle = false, bool wrap = true, string? align = null, string? spacing = null, int? maxLines = null)
    {
        var o = new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = wrap };
        if (size is not null) o["size"] = size;
        if (weight is not null) o["weight"] = weight;
        if (color is not null) o["color"] = color;
        if (subtle) o["isSubtle"] = true;
        if (align is not null) o["horizontalAlignment"] = align;
        if (spacing is not null) o["spacing"] = spacing;
        if (maxLines is not null) o["maxLines"] = maxLines;
        return o;
    }

    private static JsonObject Execute(string verb, JsonObject? data = null, string? title = null)
    {
        var o = new JsonObject { ["type"] = "Action.Execute", ["verb"] = verb };
        if (title is not null) o["title"] = title;
        o["data"] = data ?? new JsonObject();
        return o;
    }

    private static JsonObject Column(string width, params JsonNode[] items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        return new JsonObject { ["type"] = "Column", ["width"] = width, ["items"] = arr, ["verticalContentAlignment"] = "center" };
    }

    private static JsonObject ColumnSet(params JsonObject[] columns)
    {
        var arr = new JsonArray();
        foreach (var c in columns) arr.Add(c);
        return new JsonObject { ["type"] = "ColumnSet", ["columns"] = arr };
    }

    private static JsonObject Container(JsonArray items, string? style = null, string? spacing = null, JsonObject? selectAction = null, bool bleed = false)
    {
        var o = new JsonObject { ["type"] = "Container", ["items"] = items };
        if (style is not null) o["style"] = style;
        if (spacing is not null) o["spacing"] = spacing;
        if (selectAction is not null) o["selectAction"] = selectAction;
        if (bleed) o["bleed"] = true;
        return o;
    }

    private static JsonObject Actions(JsonObject card, params JsonObject[] actions)
    {
        var arr = new JsonArray();
        foreach (var a in actions) arr.Add(a);
        card["actions"] = arr;
        return card;
    }

    private const string ButtonHeight = "32px";

    /// <summary>不换行空格：小组件宿主不给带底色的 Container 加内边距，窄按钮靠它撑出左右边距。</summary>
    private const string Nbsp = "\u00A0";

    private static string Padded(string text, int n = 2) => new string('\u00A0', n) + text + new string('\u00A0', n);

    /// <summary>
    /// 小胶囊按钮：整块可点，固定最小高度让它像个按钮而不是一行字。
    /// pad 用于 auto 宽度列里的窄按钮（stretch 列里文字居中，宿主不加内边距也看不出来）。
    /// </summary>
    private static JsonObject Pill(string text, JsonObject? action, string style = "emphasis", bool subtle = false, bool pad = false)
    {
        var pill = Container(
            new JsonArray { Text(pad ? Padded(text) : text, "Small", "Bolder", align: "Center", wrap: false, subtle: subtle) },
            style: style,
            selectAction: action);
        pill["minHeight"] = ButtonHeight;
        pill["verticalContentAlignment"] = "center";
        return pill;
    }

    /// <summary>给 ColumnSet 里除第一列外的每一列加间距，让并排的按钮之间有缝。</summary>
    private static JsonObject Gap(JsonObject columnSet, string spacing = "Default")
    {
        var cols = (JsonArray)columnSet["columns"]!;
        for (int i = 1; i < cols.Count; i++)
        {
            cols[i]!["spacing"] = spacing;
        }
        return columnSet;
    }

    private static JsonObject Data(string deviceId, string? key = null, string? value = null)
    {
        var o = new JsonObject { ["deviceId"] = deviceId };
        if (key is not null) o["key"] = key;
        if (value is not null) o["value"] = value;
        return o;
    }

    // ---------------------------------------------------------------- 通用状态卡

    public static JsonObject LoginCard(string size)
    {
        var card = Card(
            Text("海尔智家", "Medium", "Bolder"),
            Text("尚未登录，请先登录海尔智家账号。", subtle: true, spacing: "Small"));
        return Actions(card, Execute(VerbLogin, title: "登录"));
    }

    public static JsonObject MessageCard(string title, string message, bool error = false, string? retryVerb = VerbRefresh, JsonObject? data = null)
    {
        var card = Card(
            Text(title, "Medium", "Bolder", error ? "Attention" : null),
            Text(message, subtle: true, spacing: "Small", maxLines: 4));
        return retryVerb is null ? card : Actions(card, Execute(retryVerb, data, "重试"));
    }

    public static JsonObject LoadingCard(string? name = null) =>
        Card(Text(name ?? "海尔智家", "Medium", "Bolder"), Text("正在加载…", subtle: true, spacing: "Small"));

    /// <summary>
    /// 总览卡：一张卡片列出账号下全部设备，每行 名称 + 状态摘要 + 电源胶囊。
    /// 点某行由 WidgetProvider 切到该设备的单设备卡（DeviceCard back=true）。
    /// </summary>
    public static JsonObject HomeCard(IReadOnlyList<DeviceState> states, string size,
        string? account, DateTime? refreshedAt, string? notice = null)
    {
        var body = new JsonArray();
        if (size != SizeSmall)
        {
            var sub = refreshedAt is null
                ? "正在获取设备状态…"
                : $"{states.Count} 台设备 · {refreshedAt:HH:mm} 更新" + (account is null ? "" : $" · {account}");
            var header = Gap(ColumnSet(
                Column("stretch",
                    Text("海尔智家", "Medium", "Bolder", maxLines: 1),
                    Text(sub, "Small", subtle: true, spacing: "None", maxLines: 1)),
                Column("auto", Pill("↻ 刷新", Execute(VerbRefresh), pad: true))));
            header["selectAction"] = Execute(VerbSettings);
            body.Add(header);
            if (notice is not null)
            {
                body.Add(Text(notice, "Small", color: "Attention", spacing: "Small", maxLines: 2));
            }
        }
        if (states.Count == 0)
        {
            body.Add(Text(refreshedAt is null ? "正在加载…" : "账号下没有设备", subtle: true));
        }
        int max = size == SizeSmall ? 3 : size == SizeMedium ? 6 : 7;
        foreach (var st in states.Take(max))
        {
            body.Add(DeviceRow(st, size));
        }
        if (states.Count > max)
        {
            body.Add(Text($"还有 {states.Count - max} 台设备未显示，请把小组件调大", "Small", subtle: true));
        }
        return CardFrom(body);
    }

    /// <summary>设备列表行：一块带底色的可点区域。大尺寸两行（名称 / 状态），其余尺寸单行。</summary>
    private static JsonObject DeviceRow(DeviceState st, string size)
    {
        var d = st.Device;
        var controls = st.Controls;
        var values = st.Values;
        var power = Capabilities.Find(controls, Kind.Power);
        bool isOn = power is null || (values.TryGetValue(power.Key, out var pv) && power.IsOn(pv));
        bool attention = st.Notice is not null || st.Error is not null;
        string status = st.Notice ?? st.Error
            ?? (controls.Count == 0 ? (st.Online ? "加载中…" : "离线") : Capabilities.Summary(controls, values, st.Online));

        var name = Text(Nbsp + d.Name, weight: "Bolder", maxLines: 1, wrap: false);
        var statusText = Text((size == SizeLarge ? Nbsp : "") + status, "Small", color: attention ? "Attention" : null, subtle: !attention, spacing: "None", maxLines: 1, wrap: false);
        var powerCol = Column("auto", PowerButton(d.Id, power, isOn, st.Online, inList: true));
        JsonObject row;
        if (size == SizeLarge)
        {
            row = Gap(ColumnSet(Column("stretch", name, statusText), powerCol));
        }
        else
        {
            statusText.Remove("spacing");
            var statusCol = Column("stretch", statusText);
            statusCol["verticalContentAlignment"] = "center";
            var nameCol = Column("auto", name);
            nameCol["verticalContentAlignment"] = "center";
            row = Gap(ColumnSet(nameCol, statusCol, powerCol));
        }

        var box = Container(new JsonArray { row }, style: "emphasis", spacing: "Default");
        box["minHeight"] = size == SizeLarge ? "44px" : ButtonHeight;
        box["verticalContentAlignment"] = "center";
        if (size != SizeSmall)
        {
            box["selectAction"] = Execute(VerbExpand, Data(d.Id));
        }
        return box;
    }

    /// <summary>设置卡（面板“自定义小组件”或点标题进入）：账号信息 + 刷新 / 退出登录。</summary>
    public static JsonObject SettingsCard(string? account, int deviceCount, DateTime? refreshedAt)
    {
        var card = Card(
            Text("海尔智家", "Medium", "Bolder"),
            Text(account is null ? "未登录" : $"账号 {account}", subtle: true, spacing: "Small"),
            Text(refreshedAt is null ? $"{deviceCount} 台设备" : $"{deviceCount} 台设备 · {refreshedAt:HH:mm} 更新", "Small", subtle: true, spacing: "None"));
        return Actions(card, Execute(VerbBack, title: "返回"), Execute(VerbRefresh, title: "刷新"), Execute(VerbLogout, title: "退出登录"));
    }

    // ---------------------------------------------------------------- 设备卡

    /// <summary>单设备卡；back=true 时左上角有“‹”返回总览（中尺寸展开某台设备时使用）。</summary>
    public static JsonObject DeviceCard(DeviceState state, string size, bool back = false)
    {
        var d = state.Device;
        var id = d.Id;
        var controls = state.Controls;
        var values = state.Values;
        var online = state.Online;

        var power = Capabilities.Find(controls, Kind.Power);
        var temp = Capabilities.Find(controls, Kind.Temperature);
        var mode = Capabilities.Find(controls, Kind.Mode);
        var fan = Capabilities.Find(controls, Kind.Fan);
        bool isOn = power is null || (values.TryGetValue(power.Key, out var pv) && power.IsOn(pv));
        bool canControl = online && isOn;

        string Val(Control? c) => c is not null && values.TryGetValue(c.Key, out var v) ? v : "";

        // 顶部：名称 + 状态
        var statusText = Capabilities.Summary(controls, values, online);
        var nameCol = Column("stretch",
            Text(d.Name, size == SizeSmall ? "Default" : "Medium", "Bolder", maxLines: 1),
            Text(statusText, "Small", subtle: true, spacing: "None", maxLines: 1));
        var powerCol = Column("auto", PowerButton(id, power, isOn, online));
        var header = Gap(back
            ? ColumnSet(Column("auto", Pill("‹", Execute(VerbBack), pad: true)), nameCol, powerCol)
            : ColumnSet(nameCol, powerCol));
        header["selectAction"] = back ? Execute(VerbBack) : Execute(VerbRefresh, Data(id));

        var body = new JsonArray { header };

        if (state.Notice is not null)
        {
            body.Add(Text(state.Notice, "Small", color: "Attention", spacing: "Small", maxLines: 2));
        }
        else if (state.Error is not null)
        {
            body.Add(Text(state.Error, "Small", color: "Attention", spacing: "Small", maxLines: 2));
        }

        if (size == SizeSmall)
        {
            if (temp is not null)
            {
                body.Add(TempRow(id, temp, Val(temp), canControl, compact: true));
            }
            else
            {
                var sensors = controls.Where(c => c.Kind == Kind.Sensor && values.ContainsKey(c.Key)).Take(2);
                foreach (var s in sensors)
                {
                    body.Add(Text($"{s.Label} {s.Format(values[s.Key])}", spacing: "Small"));
                }
            }
            return CardFrom(body);
        }

        // 中 / 大：温度 + 模式 + 风速
        if (temp is not null)
        {
            body.Add(TempRow(id, temp, Val(temp), canControl, compact: false));
        }
        bool large = size == SizeLarge;
        if (mode is not null)
        {
            body.Add(PillRow(id, mode, Val(mode), canControl, maxItems: large ? 6 : 5, labelPos: large ? LabelTop : LabelNone));
        }
        if (fan is not null)
        {
            body.Add(PillRow(id, fan, Val(fan), canControl, maxItems: large ? 6 : 4, labelPos: large ? LabelTop : LabelInline));
        }

        // 其他开关 / 传感器
        var switches = controls.Where(c => c.Kind is Kind.Switch or Kind.Button && !c.Advanced).ToList();
        var others = controls.Where(c => c.Kind is Kind.Select or Kind.Number && !c.Advanced && c.Kind != Kind.Temperature).ToList();
        var sensorsAll = controls.Where(c => c.Kind == Kind.Sensor && values.ContainsKey(c.Key)).ToList();
        if (size == SizeLarge)
        {
            if (switches.Count > 0)
            {
                body.Add(SwitchGrid(id, switches, values, canControl, columns: 2, maxItems: 8));
            }
            foreach (var c in others.Take(2))
            {
                body.Add(SelectRow(id, c, Val(c), canControl));
            }
            var extra = sensorsAll.Where(c => c.Key != "indoorTemperature").Take(3).ToList();
            if (extra.Count > 0)
            {
                body.Add(Text(string.Join(" · ", extra.Select(c => $"{c.Label} {c.Format(values[c.Key])}")), "Small", subtle: true));
            }
        }
        else
        {
            // 中尺寸只剩两行位置：布尔开关一行 3 个，带选项名的开关（上下 / 左右摆风）一行 2 个；
            // 没有摆风类开关时布尔开关占两行
            var named = switches.Where(c => !IsBoolSwitch(c)).ToList();
            var plain = switches.Where(IsBoolSwitch).ToList();
            int rows = temp is null ? 4 : 2;
            int plainRows = plain.Count == 0 ? 0 : Math.Max(1, rows - (named.Count + 1) / 2);
            int namedRows = Math.Min(rows - plainRows, (named.Count + 1) / 2);
            if (plainRows > 0)
            {
                body.Add(SwitchGrid(id, plain, values, canControl, columns: 3, maxItems: 3 * plainRows));
            }
            if (namedRows > 0)
            {
                body.Add(SwitchGrid(id, named, values, canControl, columns: 2, maxItems: 2 * namedRows));
            }
            foreach (var c in others.Take(temp is null ? 2 : 0))
            {
                body.Add(SelectRow(id, c, Val(c), canControl));
            }
        }

        var card = CardFrom(body);
        return card;
    }

    /// <summary>true/false 型开关（显示 开/关）；两项 LIST 型开关（如摆风 固定/自动）显示选项名。</summary>
    private static bool IsBoolSwitch(Control c) => c.Kind != Kind.Switch || c.OnValue == "true";

    private static JsonObject PowerButton(string id, Control? power, bool isOn, bool online, bool inList = false)
    {
        if (power is null)
        {
            return Text(online ? "在线" : "离线", "Small", subtle: true);
        }
        var label = isOn ? "开" : "关";
        return Pill($"⏻ {label}", online ? Execute(VerbSet, Data(id, power.Key, isOn ? power.OffValue : power.OnValue)) : null,
            style: isOn ? "accent" : (inList ? "default" : "emphasis"), subtle: !online, pad: true);
    }

    private static JsonObject TempRow(string id, Control temp, string value, bool enabled, bool compact)
    {
        var shown = string.IsNullOrEmpty(value) ? "--" : value;
        var big = Text(shown + "°", compact ? "ExtraLarge" : "ExtraLarge", "Bolder", enabled ? null : "Default", subtle: !enabled, align: "Center", wrap: false);
        var minus = StepButton(id, temp, value, -temp.Step, "−", enabled);
        var plus = StepButton(id, temp, value, +temp.Step, "+", enabled);
        var row = Gap(ColumnSet(
            Column("auto", minus),
            Column("stretch", big, Text(temp.Label, "Small", subtle: true, align: "Center", spacing: "None", wrap: false)),
            Column("auto", plus)), "Medium");
        row["spacing"] = compact ? "Small" : "Medium";
        return row;
    }

    private static JsonObject StepButton(string id, Control temp, string value, double delta, string glyph, bool enabled)
    {
        double cur = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (temp.MinValue + temp.MaxValue) / 2;
        double next = Math.Clamp(cur + delta, temp.MinValue, temp.MaxValue);
        bool active = enabled && Math.Abs(next - cur) > 1e-6;
        var text = Text(Padded(glyph, 3), "Large", "Bolder", align: "Center", wrap: false, subtle: !active);
        var box = Container(new JsonArray { text }, style: "emphasis");
        if (active)
        {
            box["selectAction"] = Execute(VerbSet, Data(id, temp.Key, FormatNumber(next)));
        }
        box["minHeight"] = "44px";
        box["verticalContentAlignment"] = "center";
        return box;
    }

    private static string FormatNumber(double n) =>
        Math.Abs(n - Math.Round(n)) < 1e-6 ? ((long)Math.Round(n)).ToString() : n.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>模式 / 风速：一行胶囊按钮，当前值高亮。</summary>
    private const int LabelTop = 0;
    private const int LabelInline = 1;
    private const int LabelNone = 2;

    private static JsonObject PillRow(string id, Control ctrl, string value, bool enabled, int maxItems, int labelPos = LabelTop)
    {
        var cols = new List<JsonObject>();
        if (labelPos == LabelInline)
        {
            var lc = Column("auto", Text(ctrl.Label, "Small", subtle: true, wrap: false));
            lc["verticalContentAlignment"] = "center";
            cols.Add(lc);
        }
        foreach (var (v, label) in ctrl.Options.Take(maxItems))
        {
            bool selected = v == value;
            var pill = Pill(label, enabled && !selected ? Execute(VerbSet, Data(id, ctrl.Key, v)) : null,
                style: selected ? "accent" : "emphasis", subtle: !enabled && !selected);
            cols.Add(Column("stretch", pill));
        }
        var row = Gap(ColumnSet(cols.ToArray()));
        row["spacing"] = "Small";
        if (labelPos != LabelTop)
        {
            return row;
        }
        var wrap = new JsonArray
        {
            Text(ctrl.Label, "Small", subtle: true, spacing: "Small"),
            row,
        };
        var c = Container(wrap, spacing: "Small");
        return c;
    }

    /// <summary>开关网格：每格显示名称 + 开/关，点击切换；按钮型控件点击即发送。</summary>
    private static JsonObject SwitchGrid(string id, List<Control> switches, IReadOnlyDictionary<string, string> values, bool enabled, int columns, int maxItems)
    {
        var items = switches.Take(maxItems).ToList();
        var body = new JsonArray();
        for (int i = 0; i < items.Count; i += columns)
        {
            var cols = new List<JsonObject>();
            for (int j = 0; j < columns; j++)
            {
                if (i + j >= items.Count)
                {
                    cols.Add(Column("stretch", new JsonObject { ["type"] = "TextBlock", ["text"] = " " }));
                    continue;
                }
                var c = items[i + j];
                JsonObject cell;
                if (c.Kind == Kind.Button)
                {
                    cell = Pill(c.Label, enabled ? Execute(VerbSet, Data(id, c.Key, c.Options.FirstOrDefault().Value ?? c.Key)) : null);
                }
                else
                {
                    bool on = values.TryGetValue(c.Key, out var v) && c.IsOn(v);
                    var stateText = IsBoolSwitch(c) ? (on ? "开" : "关") : c.OptionLabel(v);
                    cell = Pill($"{c.Label} {stateText}", enabled ? Execute(VerbSet, Data(id, c.Key, on ? c.OffValue : c.OnValue)) : null,
                        style: on ? "accent" : "emphasis", subtle: !enabled && !on);
                }
                cols.Add(Column("stretch", cell));
            }
            var row = Gap(ColumnSet(cols.ToArray()));
            row["spacing"] = "Small";
            body.Add(row);
        }
        return Container(body, spacing: "Small");
    }

    /// <summary>多选项：显示当前值，点击循环切换到下一项。</summary>
    private static JsonObject SelectRow(string id, Control c, string value, bool enabled)
    {
        string next = value;
        if (c.Options.Count > 0)
        {
            int idx = c.Options.FindIndex(o => o.Value == value);
            next = c.Options[(idx + 1) % c.Options.Count].Value;
        }
        else if (c.Kind == Kind.Number)
        {
            double cur = double.TryParse(value, out var v) ? v : c.MinValue;
            double n = cur + c.Step;
            if (n > c.MaxValue) n = c.MinValue;
            next = FormatNumber(n);
        }
        var row = ColumnSet(
            Column("stretch", Text(c.Label, "Small", wrap: false)),
            Column("auto", Text(c.Format(value) + "  ›", "Small", "Bolder", wrap: false)));
        var cell = Container(new JsonArray { row }, style: "emphasis", spacing: "Small",
            selectAction: enabled ? Execute(VerbSet, Data(id, c.Key, next)) : null);
        cell["minHeight"] = ButtonHeight;
        cell["verticalContentAlignment"] = "center";
        return cell;
    }
}
