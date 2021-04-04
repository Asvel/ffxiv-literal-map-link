using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Game.Chat.SeStringHandling;

namespace LiteralMapLink
{
    public class PreMapLinkPayload : Payload
    {
        public override PayloadType Type => PayloadType.AutoTranslateText;

        private readonly uint territoryTypeId;
        private readonly uint mapId;
        private readonly int rawX;
        private readonly int rawY;
        private readonly int rawZ;

        public PreMapLinkPayload(uint territoryTypeId, uint mapId, int rawX, int rawY)
        {
            this.territoryTypeId = territoryTypeId;
            this.mapId = mapId;
            this.rawX = rawX;
            this.rawY = rawY;
            this.rawZ = -30000;
        }

        protected override byte[] EncodeImpl()
        {
            var territoryBytes = MakeInteger(this.territoryTypeId);
            var mapBytes = MakeInteger(this.mapId);
            if (territoryBytes.Length == 2 && mapBytes.Length == 2)
            {
                territoryBytes = new byte[] { territoryBytes[1] };
                mapBytes = new byte[] { mapBytes[1] };
            }

            var xBytes = MakeInteger(unchecked((uint)this.rawX));
            var yBytes = MakeInteger(unchecked((uint)this.rawY));
            var zBytes = MakeInteger(unchecked((uint)this.rawZ));

            var chunkLen = 3 + territoryBytes.Length + mapBytes.Length + xBytes.Length + yBytes.Length + zBytes.Length;

            var bytes = new List<byte>()
            {
                START_BYTE,
                (byte)SeStringChunkType.AutoTranslateKey, (byte)chunkLen, 0xC9, 0x04
            };
            bytes.AddRange(territoryBytes);
            bytes.AddRange(mapBytes);
            bytes.AddRange(xBytes);
            bytes.AddRange(yBytes);
            bytes.AddRange(zBytes);
            bytes.Add(END_BYTE);

            return bytes.ToArray();
        }

        protected override void DecodeImpl(BinaryReader reader, long endOfStream)
        {
            throw new NotImplementedException();
        }

        protected override byte GetMarkerForIntegerBytes(byte[] bytes)
        {
            var type = bytes.Length switch
            {
                3 => (byte)IntegerType.Int24Special,
                2 => (byte)IntegerType.Int16,
                1 => (byte)IntegerType.None,
                _ => base.GetMarkerForIntegerBytes(bytes)
            };

            return type;
        }
    }
}
