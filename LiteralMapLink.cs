using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Reflection;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.GeneratedSheets;

namespace LiteralMapLink
{
    public class LiteralMapLink : IDalamudPlugin
    {
        [PluginService]
        [RequiredVersion("1.0")]
        private DalamudPluginInterface PluginInterface { get; init; }

        [PluginService]
        [RequiredVersion("1.0")]
        private IGameInteropProvider GameInteropProvider { get; init; }

        [PluginService]
        [RequiredVersion("1.0")]
        private IChatGui Chat { get; init; }

        [PluginService]
        [RequiredVersion("1.0")]
        private IDataManager Data { get; init; }

        [PluginService]
        [RequiredVersion("1.0")]
        private IPluginLog PluginLog { get; init; }

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr ParseMessageDelegate(IntPtr a, IntPtr b);
        private readonly Hook<ParseMessageDelegate> parseMessageHook;

        private readonly Dictionary<string, (uint, uint)> maps = new();
        private readonly Dictionary<string, string> unmaskedMapNames = new()
        {
            { "狼狱演*场", "狼狱演习场" },
            { "魔**阿济兹拉", "魔大陆阿济兹拉" },
            { "玛托雅的洞*", "玛托雅的洞穴" },
            { "魔**中枢", "魔大陆中枢" },
            { "双蛇*军营", "双蛇党军营" },
            { "地衣宫演*场", "地衣宫演习场" },
            { "水晶塔演*场", "水晶塔演习场" },
            { "*泉神社", "醴泉神社" },
            { "*泉神社神道", "醴泉神社神道" },
            { "格**火山", "格鲁格火山" },
            { "**亚马乌罗提", "末日亚马乌罗提" },
            { "游末邦**", "游末邦监狱" },
        };

        private readonly Regex mapLinkPattern = new(
            @"\uE0BB(?<map>.+?)(?<instance>[\ue0b1-\ue0b9])? \( (?<x>\d{1,2}\.\d)  , (?<y>\d{1,2}\.\d) \)",
            RegexOptions.Compiled);

        private readonly FieldInfo territoryTypeIdField = typeof(MapLinkPayload).GetField("territoryTypeId",
            BindingFlags.NonPublic | BindingFlags.Instance);
        private readonly FieldInfo mapIdField = typeof(MapLinkPayload).GetField("mapId",
            BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly Dictionary<string, (uint, uint, int, int)> historyCoordinates = new();

        public LiteralMapLink()
        {
            this.parseMessageHook = GameInteropProvider.HookFromSignature<ParseMessageDelegate>(
                "E8 ???????? 48 8B D0 48 8D 4C 24 30 E8 ???????? 48 8B 44 24 30 80 38 00 0F 84", HandleParseMessageDetour);
            this.parseMessageHook.Enable();

            this.Chat.ChatMessage += HandleChatMessage;

            foreach (var territoryType in this.Data.GetExcelSheet<TerritoryType>())
            {
                var name = territoryType.PlaceName.Value.Name.RawString;
                if (name != "" && !this.maps.ContainsKey(name))
                {
                    this.maps.Add(name, (territoryType.RowId, territoryType.Map.Row));
                }
            }
        }

        private IntPtr HandleParseMessageDetour(IntPtr a, IntPtr b)
        {
            var ret = this.parseMessageHook.Original(a, b);
            try
            {
                var pMessage = Marshal.ReadIntPtr(ret);
                var length = 0;
                while (Marshal.ReadByte(pMessage, length) != 0) length++;
                var message = new byte[length];
                Marshal.Copy(pMessage, message, 0, length);

                var parsed = SeString.Parse(message);
                foreach (var payload in parsed.Payloads)
                {
                    if (payload is AutoTranslatePayload p && p.Encode()[3] == 0xC9 && p.Encode()[4] == 0x04)
                    {
                        if (this.PluginInterface.IsDebugging) PluginLog.Info("<- {0}", BitConverter.ToString(message));
                        return ret;
                    }
                }
                for (var i = 0; i < parsed.Payloads.Count; i++)
                {
                    if (parsed.Payloads[i] is not TextPayload payload) continue;
                    var match = mapLinkPattern.Match(payload.Text);
                    if (!match.Success) continue;

                    var mapName = match.Groups["map"].Value;
                    if (unmaskedMapNames.ContainsKey(mapName))
                    {
                        mapName = unmaskedMapNames[mapName];
                    }
                    var historyKey = string.Concat(mapName, match.Value.AsSpan(mapName.Length + 1));

                    uint territoryId, mapId;
                    int rawX, rawY;
                    if (this.historyCoordinates.TryGetValue(historyKey, out var history))
                    {
                        (territoryId, mapId, rawX, rawY) = history;
                        PluginLog.Info("recall {0} => {1}", historyKey, history);
                    }
                    else
                    {
                        if (!this.maps.TryGetValue(mapName, out var mapInfo))
                        {
                            PluginLog.Warning("Can't find map {0}", mapName);
                            continue;
                        }
                        (territoryId, mapId) = mapInfo;
                        var map = this.Data.GetExcelSheet<Map>().GetRow(mapId);
                        rawX = this.GenerateRawPosition(float.Parse(match.Groups["x"].Value), map.OffsetX, map.SizeFactor);
                        rawY = this.GenerateRawPosition(float.Parse(match.Groups["y"].Value), map.OffsetY, map.SizeFactor);
                        if (match.Groups["instance"].Value != "")
                        {
                            mapId |= (match.Groups["instance"].Value[0] - 0xe0b0u) << 16;
                        }
                        history = (territoryId, mapId, rawX, rawY);
                        this.historyCoordinates[historyKey] = history;
                        PluginLog.Info("generate {0} => {1}", historyKey, history);
                    }

                    var newPayloads = new List<Payload>();
                    if (match.Index > 0)
                    {
                        newPayloads.Add(new TextPayload(payload.Text[..match.Index]));
                    }
                    newPayloads.Add(new PreMapLinkPayload(territoryId, mapId, rawX, rawY));
                    if (match.Index + match.Length < payload.Text.Length)
                    {
                        newPayloads.Add(new TextPayload(payload.Text[(match.Index + match.Length)..]));
                    }
                    parsed.Payloads.RemoveAt(i);
                    parsed.Payloads.InsertRange(i, newPayloads);

                    var newMessage = parsed.Encode();
                    if (this.PluginInterface.IsDebugging) PluginLog.Info("-> {0}", BitConverter.ToString(newMessage));
                    var messageCapacity = Marshal.ReadInt64(ret + 8);
                    if (newMessage.Length + 1 > messageCapacity)
                    {
                        // FIXME: should call std::string#resize(or maybe _Reallocate_grow_by) here, but haven't found the signature yet
                        PluginLog.Error("Sorry, message capacity not enough, abort conversion for {0}", historyKey);
                        return ret;
                    }
                    Marshal.WriteInt64(ret + 16, newMessage.Length + 1);
                    Marshal.Copy(newMessage, 0, pMessage, newMessage.Length);
                    Marshal.WriteByte(pMessage, newMessage.Length, 0x00);

                    break;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Exception on HandleParseMessageDetour.");
            }
            return ret;
        }

        private void HandleChatMessage(XivChatType type, uint senderId, ref SeString sender, ref SeString message, ref bool isHandled)
        {
            try
            {
                for (var i = 0; i < message.Payloads.Count; i++)
                {
                    if (message.Payloads[i] is not MapLinkPayload payload) continue;
                    if (message.Payloads[i + 6] is not TextPayload payloadText) continue;

                    var territoryId = (uint)territoryTypeIdField.GetValue(payload);
                    var mapId = (uint)mapIdField.GetValue(payload);
                    var historyKey = payloadText.Text[..(payloadText.Text.LastIndexOf(")") + 1)];
                    var mapName = historyKey[..(historyKey.LastIndexOf("(") - 1)];
                    if ('\ue0b1' <= mapName[^1] && mapName[^1] <= '\ue0b9')
                    {
                        this.maps[mapName[0..^1]] = (territoryId, mapId);
                        mapId |= (mapName[^1] - 0xe0b0u) << 16;
                    }
                    else
                    {
                        this.maps[mapName] = (territoryId, mapId);
                    }
                    var history = (territoryId, mapId, payload.RawX, payload.RawY);
                    this.historyCoordinates[historyKey] = history;
                    PluginLog.Info("memorize {0} => {1}", historyKey, history);
                    //PluginLog.Info(BitConverter.ToString(payload.Encode()));
                    //PluginLog.Info(BitConverter.ToString(payload.Encode(true)));
                }
            }
            catch (Exception ex)
            {
                PluginLog.Debug(ex, "Exception on HandleChatMessage.");
            }
        }

        #region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            this.parseMessageHook.Dispose();
            this.Chat.ChatMessage -= HandleChatMessage;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion

        private readonly Random random = new();
        public int GenerateRawPosition(float visibleCoordinate, short offset, ushort factor)
        {
            visibleCoordinate += (float)random.NextDouble() * 0.07f;
            var scale = factor / 100.0f;
            var scaledPos = ((((visibleCoordinate - 1.0f) * scale / 41.0f) * 2048.0f) - 1024.0f) / scale;
            return (int)Math.Ceiling(scaledPos - offset) * 1000;
        }
    }
}
