using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Hooking;
using Dalamud.Game.Chat;
using Dalamud.Game.Chat.SeStringHandling;
using Dalamud.Game.Chat.SeStringHandling.Payloads;
using Lumina.Excel.GeneratedSheets;

namespace LiteralMapLink
{
    public class LiteralMapLink : IDalamudPlugin
    {
        private DalamudPluginInterface pluginInterface;

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr ParseMessageDelegate(IntPtr a, IntPtr b);
        private Hook<ParseMessageDelegate> parseMessageHook;

        private readonly Dictionary<string, (uint, uint)> maps = new();

        private readonly Regex mapLinkPattern = new(
            @"\uE0BB(?<map>.+?) \( (?<x>\d{1,2}\.\d)  , (?<y>\d{1,2}\.\d) \)",
            RegexOptions.Compiled);

        private readonly FieldInfo territoryTypeIdField = typeof(MapLinkPayload).GetField("territoryTypeId",
            BindingFlags.NonPublic | BindingFlags.Instance);
        private readonly FieldInfo mapIdField = typeof(MapLinkPayload).GetField("mapId",
            BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly Dictionary<(uint, uint, int, int), (int, int)> historyCoordinates = new();

        public string Name => "Literal Map Link";

        public void Initialize(DalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;

            var parseMessageAddress = this.pluginInterface.TargetModuleScanner.ScanText(
                "E8 ???????? 48 8B D0 48 8D 4C 24 30 E8 ???????? 48 8B 44 24 30 80 38 00 0F 84");
            this.parseMessageHook = new(parseMessageAddress, new ParseMessageDelegate(HandleParseMessageDetour), this);
            this.parseMessageHook.Enable();

            this.pluginInterface.Framework.Gui.Chat.OnChatMessage += HandleChatMessage;

            foreach (var territoryType in this.pluginInterface.Data.GetExcelSheet<TerritoryType>())
            {
                var name = territoryType.PlaceName.Value.Name.RawString;
                if (name != "" && !this.maps.ContainsKey(name))
                {
                    this.maps.Add(name, (territoryType.RowId, territoryType.Map.Row));
                }
            }
            if (this.maps.TryGetValue("狼狱演习场", out var map)) this.maps.Add("狼狱演*场", map);
            if (this.maps.TryGetValue("魔大陆阿济兹拉", out map)) this.maps.Add("魔**阿济兹拉", map);
            if (this.maps.TryGetValue("玛托雅的洞穴", out map)) this.maps.Add("玛托雅的洞*", map);
            if (this.maps.TryGetValue("魔大陆中枢", out map)) this.maps.Add("魔**中枢", map);
            if (this.maps.TryGetValue("双蛇党军营", out map)) this.maps.Add("双蛇*军营", map);
            if (this.maps.TryGetValue("地衣宫演习场", out map)) this.maps.Add("地衣宫演*场", map);
            if (this.maps.TryGetValue("水晶塔演习场", out map)) this.maps.Add("水晶塔演*场", map);
            if (this.maps.TryGetValue("醴泉神社", out map)) this.maps.Add("*泉神社", map);
            if (this.maps.TryGetValue("醴泉神社神道", out map)) this.maps.Add("*泉神社神道", map);
            if (this.maps.TryGetValue("格鲁格火山", out map)) this.maps.Add("格**火山", map);
            if (this.maps.TryGetValue("末日亚马乌罗提", out map)) this.maps.Add("**亚马乌罗提", map);
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

                var parsed = this.pluginInterface.SeStringManager.Parse(message);
                foreach (var payload in parsed.Payloads)
                {
                    if (payload is AutoTranslatePayload p && p.Encode()[3] == 0xC9 && p.Encode()[4] == 0x04)
                    {
                        if (this.pluginInterface.IsDebugging) PluginLog.Log("<- {0}", BitConverter.ToString(message));
                        return ret;
                    }
                }
                for (var i = 0; i < parsed.Payloads.Count; i++)
                {
                    if (!(parsed.Payloads[i] is TextPayload payload)) continue;
                    var match = mapLinkPattern.Match(payload.Text);
                    if (!match.Success) continue;
                    if (!this.maps.TryGetValue(match.Groups["map"].Value, out var mapInfo))
                    {
                        PluginLog.Warning("Can't find map {0}", match.Groups["map"].Value);
                        continue;
                    }

                    var (territoryId, mapId) = mapInfo;
                    var inputX = float.Parse(match.Groups["x"].Value) + 1e-5f;
                    var inputY = float.Parse(match.Groups["y"].Value) + 1e-5f;
                    var historyKey = (territoryId, mapId, (int)(inputX * 10.0f), (int)(inputY * 10.0f));
                    int rawX, rawY;
                    if (this.historyCoordinates.TryGetValue(historyKey, out var history))
                    {
                        PluginLog.Log("{0} found {1}", historyKey, history);
                        (rawX, rawY) = history;
                    }
                    else
                    {
                        PluginLog.Log("{0} not found", historyKey);
                        var map = this.pluginInterface.Data.GetExcelSheet<Map>().GetRow(mapId);
                        rawX = this.GenerateRawPosition(inputX, map.OffsetX, map.SizeFactor);
                        rawY = this.GenerateRawPosition(inputY, map.OffsetY, map.SizeFactor);
                        this.historyCoordinates[historyKey] = (rawX, rawY);
                    }
                    PluginLog.Log("{0} => {1:X} {2:X} ({3},{4})", match.Value, territoryId, mapId, rawX, rawY);

                    var newPayloads = new List<Payload>();
                    if (match.Index > 0)
                    {
                        newPayloads.Add(new TextPayload(payload.Text.Substring(0, match.Index)));
                    }
                    newPayloads.Add(new PreMapLinkPayload(territoryId, mapId, rawX, rawY));
                    if (match.Index + match.Length < payload.Text.Length)
                    {
                        newPayloads.Add(new TextPayload(payload.Text.Substring(match.Index + match.Length)));
                    }
                    parsed.Payloads.RemoveAt(i);
                    parsed.Payloads.InsertRange(i, newPayloads);

                    var newMessage = parsed.Encode();
                    if (this.pluginInterface.IsDebugging) PluginLog.Log("-> {0}", BitConverter.ToString(newMessage));
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
            foreach (var payload in message.Payloads)
            {
                if (payload is MapLinkPayload p)
                {
                    var territoryId = (uint)territoryTypeIdField.GetValue(p);
                    var mapId = (uint)mapIdField.GetValue(p);
                    this.maps[p.PlaceName] = (territoryId, mapId);
                    var visibleX = (int)((p.XCoord + 1e-5f) * 10.0f);
                    var visibleY = (int)((p.YCoord + 1e-5f) * 10.0f);
                    this.historyCoordinates[(territoryId, mapId, visibleX, visibleY)] = (p.RawX, p.RawY);
                    PluginLog.Log("memorize ({0},{1},{2},{3}) => ({4},{5})", territoryId, mapId, visibleX, visibleY, p.RawX, p.RawY);
                    //PluginLog.Log(BitConverter.ToString(p.Encode()));
                    //PluginLog.Log(BitConverter.ToString(p.Encode(true)));
                }
            }
        }

        #region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            this.parseMessageHook.Dispose();
            this.pluginInterface.Framework.Gui.Chat.OnChatMessage -= HandleChatMessage;
            this.pluginInterface.Dispose();
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
