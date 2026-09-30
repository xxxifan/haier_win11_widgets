using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using HaierWidget.Haier;
using HaierWidget.Widget;

namespace HaierWidget.Util;

/// <summary>离线自检（--selftest）：签名、网关编解码、能力解析、卡片生成。</summary>
public static class SelfTest
{
    private static int _failed;

    public static int Run()
    {
        Check("签名与 Python 版一致", () =>
        {
            var sign = HaierApi.Sign("MB-UZHSH-0001", "5dfca8714eb26e3a776e58a8273c8752", "1700000000000",
                "{\"username\": \"a\", \"password\": \"b\"}", "https://zj.haier.net/api-gw/oauthserver/account/v1/login");
            Expect(sign == ExpectedSign, $"sign={sign}");
        });

        Check("DigitalModel 解码 (base64+gzip)", () =>
        {
            var json = "{\"attributes\":[{\"name\":\"onOffStatus\",\"value\":\"true\"},{\"name\":\"targetTemperature\",\"value\":\"24\"}]}";
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                gz.Write(Encoding.UTF8.GetBytes(json));
            }
            var values = HaierGateway.DecodeDigitalModel(Convert.ToBase64String(ms.ToArray()));
            Expect(values["onOffStatus"] == "true" && values["targetTemperature"] == "24", string.Join(",", values));
        });

        Check("BatchCmdReq 结构", () =>
        {
            var cmd = HaierGateway.BuildBatchCmd("tok", "dev1", new Dictionary<string, string> { ["onOffStatus"] = "true" }, "abc");
            var d = cmd["content"]!["data"]![0]!;
            Expect(Json.Str(cmd["topic"]) == "BatchCmdReq" && Json.Str(d["subSn"]) == "abc:0"
                && Json.Str(d["cmdArgs"]!["onOffStatus"]) == "true" && Json.Str(d["deviceId"]) == "dev1", cmd.ToJsonString());
        });

        Check("会话文件与 Python 版格式互通", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "haier_widget_selftest_session.json");
            File.WriteAllText(path, "{\n  \"client_id\": \"138\",\n  \"token\": \"t\",\n  \"refresh_token\": \"r\",\n  \"expires_at\": 1791477875,\n  \"app_source\": \"app\",\n  \"user\": {\"userId\": 1, \"mobile\": \"13800001234\", \"username\": \"u\"}\n}");
            var s = HaierSession.Load(path)!;
            Expect(s.ClientId == "138" && s.MaskedAccount == "138****1234" && s.ExpiresAt == 1791477875, s.MaskedAccount);
            s.Save(path);
            var again = HaierSession.Load(path)!;
            Expect(again.Token == "t" && Json.Str(again.User?["mobile"]) == "13800001234", "roundtrip");
            File.Delete(path);
        });

        var controls = Capabilities.BuildControls(AcAttributes());
        var byKey = controls.ToDictionary(c => c.Key);

        Check("能力解析：隐藏与类型", () =>
        {
            Expect(!byKey.ContainsKey("getAllProperty"), "getAllProperty 应隐藏");
            Expect(byKey["onOffStatus"].Kind == Kind.Power, "power");
            Expect(byKey["targetTemperature"].Kind == Kind.Temperature, "temperature");
            Expect(byKey["operationMode"].Kind == Kind.Mode, "mode");
            Expect(byKey["windSpeed"].Kind == Kind.Fan, "fan");
            Expect(byKey["indoorTemperature"].Kind == Kind.Sensor, "sensor");
            Expect(byKey["healthMode"].Kind == Kind.Switch, "switch");
            Expect(byKey["stopCurrentAlarm"].Kind == Kind.Button, "button");
            Expect(byKey["targetHumidity"].Kind == Kind.Select && byKey["targetHumidity"].Advanced, "select advanced");
        });

        Check("能力解析：温度范围 / 模式排序 / 两项开关", () =>
        {
            var t = byKey["targetTemperature"];
            Expect(t.MinValue == 16 && t.MaxValue == 30 && t.Step == 1 && t.Unit == "°C", $"{t.MinValue}-{t.MaxValue}");
            var mode = byKey["operationMode"];
            Expect(string.Join(",", mode.Options.Select(o => o.Value)) == "0,1,2,4,6", string.Join(",", mode.Options.Select(o => o.Value)));
            Expect(mode.OptionLabel("0") == "自动", mode.OptionLabel("0"));
            var swing = byKey["windDirectionVertical"];
            Expect(swing.Kind == Kind.Switch && swing.OffValue == "0" && swing.OnValue == "8" && swing.IsOn("8") && !swing.IsOn("0"), "swing");
            Expect(byKey["healthMode"].Label == "负离子", byKey["healthMode"].Label);
        });

        Check("状态摘要", () =>
        {
            var values = new Dictionary<string, string>
            {
                ["onOffStatus"] = "true", ["operationMode"] = "1", ["targetTemperature"] = "24",
                ["windSpeed"] = "5", ["indoorTemperature"] = "25",
            };
            Expect(Capabilities.Summary(controls, values) == "制冷 · 24°C · 风速自动 · 室温 25°C", Capabilities.Summary(controls, values));
            values["onOffStatus"] = "false";
            Expect(Capabilities.Summary(controls, values) == "已关机 · 室温 25°C", Capabilities.Summary(controls, values));
            Expect(Capabilities.Summary(controls, values, online: false) == "离线", "offline");
            Expect(Capabilities.Summary(controls, new Dictionary<string, string>()) == "等待数据…", "empty");
        });

        Check("型号缓存合并", () =>
        {
            var cached = new JsonArray { new JsonObject { ["name"] = "a", ["desc"] = "old" }, new JsonObject { ["name"] = "b" } };
            var fresh = new JsonArray { new JsonObject { ["name"] = "a", ["desc"] = "new" }, new JsonObject { ["name"] = "c" } };
            var merged = Capabilities.MergeAttributes(cached, fresh);
            Expect(merged.Count == 3 && Json.Str(merged[0]!["desc"]) == "new" && Json.Str(merged[2]!["name"]) == "c", merged.ToJsonString());
        });

        Check("关机时保留可写定义（温度控件不消失）", () =>
        {
            // 云端在关机时会把目标温度标成只读，直接覆盖会让温度行从卡片上消失
            var off = new JsonArray();
            foreach (var a in AcAttributes())
            {
                var o = (JsonObject)a!.DeepClone();
                if (Json.Str(o["name"]) is "targetTemperature" or "operationMode" or "windSpeed")
                {
                    o["writable"] = false;
                }
                off.Add(o);
            }
            var offOnly = Capabilities.BuildControls(Capabilities.MergeAttributes(null, off)).ToDictionary(c => c.Key);
            Expect(offOnly["targetTemperature"].Kind == Kind.Sensor, "无缓存时关机定义应为只读");

            var merged = Capabilities.MergeAttributes(AcAttributes(), off);
            var after = Capabilities.BuildControls(merged).ToDictionary(c => c.Key);
            Expect(after["targetTemperature"].Kind == Kind.Temperature && after["targetTemperature"].MaxValue == 30, "温度控件应保留");
            Expect(after["operationMode"].Kind == Kind.Mode && after["windSpeed"].Kind == Kind.Fan, "模式 / 风速应保留");
            Expect(!Json.Bool(merged.First(a => Json.Str(a!["name"]) == "indoorTemperature")!["writable"]), "只读属性不应被置为可写");
        });

        Check("卡片生成", () =>
        {
            var state = new DeviceState
            {
                Device = new DeviceInfo { Id = "dev1", Name = "主卧空调", ProductName = "RFTS71MX-V1", Room = "主卧" },
                Controls = controls,
                Online = true,
            };
            foreach (var (k, v) in new Dictionary<string, string>
                     {
                         ["onOffStatus"] = "true", ["operationMode"] = "1", ["targetTemperature"] = "24",
                         ["windSpeed"] = "5", ["indoorTemperature"] = "25", ["healthMode"] = "false", ["windDirectionVertical"] = "8",
                     })
            {
                state.Values[k] = v;
            }
            foreach (var size in new[] { CardBuilder.SizeSmall, CardBuilder.SizeMedium, CardBuilder.SizeLarge })
            {
                var json = CardBuilder.DeviceCard(state, size).ToJsonString(HaierApi.CompactJson);
                Expect(json.Contains("\"verb\":\"set\"") && json.Contains("\"key\":\"onOffStatus\"") && json.Contains("24°"), $"{size}: {json[..Math.Min(200, json.Length)]}");
                if (size != CardBuilder.SizeSmall)
                {
                    Expect(json.Contains("\"key\":\"operationMode\"") && json.Contains("\"key\":\"windSpeed\""), $"{size} 缺少模式/风速");
                }
                if (size == CardBuilder.SizeLarge)
                {
                    Expect(json.Contains("\"key\":\"healthMode\"") && json.Contains("\"key\":\"stopCurrentAlarm\"") == false, $"{size} 开关");
                }
            }
            var home = CardBuilder.HomeCard(new[] { state }, CardBuilder.SizeMedium, "138****1234", DateTime.Now).ToJsonString(HaierApi.CompactJson);
            Expect(home.Contains("\"verb\":\"expand\"") && home.Contains("主卧空调") && home.Contains("\"key\":\"onOffStatus\"") && !home.Contains("\"key\":\"targetTemperature\""), "home 列表");
            var small = CardBuilder.HomeCard(new[] { state }, CardBuilder.SizeSmall, "138****1234", DateTime.Now).ToJsonString(HaierApi.CompactJson);
            Expect(!small.Contains("\"verb\":\"expand\"") && small.Contains("\"key\":\"onOffStatus\""), "home small");
            var focus = CardBuilder.DeviceCard(state, CardBuilder.SizeMedium, back: true).ToJsonString(HaierApi.CompactJson);
            Expect(focus.Contains("\"verb\":\"back\"") && focus.Contains("\"key\":\"windDirectionVertical\""), "device back + 摆风");
            Expect(CardBuilder.SettingsCard("138****1234", 1, DateTime.Now).ToJsonString(HaierApi.CompactJson).Contains("\"verb\":\"logout\""), "settings");
            Expect(CardBuilder.LoginCard(CardBuilder.SizeSmall).ToJsonString().Contains("\"verb\":\"login\""), "login");
        });

        Console.WriteLine(_failed == 0 ? "自检全部通过" : $"自检失败 {_failed} 项");
        return _failed == 0 ? 0 : 1;
    }

    private const string ExpectedSign = "cfccecfb307673c19802c14940c74f0262fd7ab443b66b13a82fe859acb93cea";

    private static void Check(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"[通过] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[失败] {name}: {ex.Message}");
        }
    }

    private static void Expect(bool cond, string detail)
    {
        if (!cond)
        {
            throw new Exception(detail);
        }
    }

    private static JsonObject ListAttr(string name, string desc, params (string, string?)[] items)
    {
        var list = new JsonArray();
        foreach (var (d, t) in items)
        {
            list.Add(new JsonObject { ["data"] = d, ["desc"] = t });
        }
        return new JsonObject
        {
            ["name"] = name, ["desc"] = desc, ["readable"] = true, ["writable"] = true,
            ["valueRange"] = new JsonObject { ["type"] = "LIST", ["dataList"] = list },
            ["value"] = items[0].Item1,
        };
    }

    private static JsonObject StepAttr(string name, string desc, bool writable, string min, string max, string value) => new()
    {
        ["name"] = name, ["desc"] = desc, ["readable"] = true, ["writable"] = writable, ["value"] = value,
        ["valueRange"] = new JsonObject
        {
            ["type"] = "STEP",
            ["dataStep"] = new JsonObject { ["dataType"] = "Integer", ["minValue"] = min, ["maxValue"] = max, ["step"] = "1" },
        },
    };

    /// <summary>RFTS71MX-V1 中央空调内机的真实属性片段（与 tests/test_capabilities.py 相同）。</summary>
    private static JsonArray AcAttributes()
    {
        var humidity = Enumerable.Range(0, 91).Select(i => (i.ToString(), (string?)$"{i}%")).ToArray();
        return new JsonArray
        {
            ListAttr("getAllProperty", "查询所有属性", ("getAllProperty", null)),
            ListAttr("onOffStatus", "开关机状态", ("false", "关机"), ("true", "开机")),
            StepAttr("targetTemperature", "目标温度", true, "16", "30", "24"),
            ListAttr("operationMode", "功能模式", ("0", "智能/自动/舒适"), ("1", "制冷"), ("4", "制热"), ("6", "送风"), ("2", "除湿")),
            ListAttr("windSpeed", "风速", ("1", "高"), ("2", "中"), ("3", "低"), ("5", "自动")),
            StepAttr("indoorTemperature", "当前室内温度", false, "0", "55", "25"),
            ListAttr("healthMode", "健康（负离子）模式", ("true", "开"), ("false", "关")),
            ListAttr("windDirectionVertical", "上下摆风", ("0", "上下摆位置固定"), ("8", "上下摆自动")),
            ListAttr("stopCurrentAlarm", "停止报警", ("stopCurrentAlarm", null)),
            ListAttr("targetHumidity", "目标湿度", humidity),
        };
    }
}
