using System;
using SteamKit2.GC.CSGO.Internal;
using System.Collections.Generic;

namespace SteamInventoryManager.Common
{

    public class CSGOItem
    {
        private ulong id;
        private string sprayName;
        private string sprayDescription;
        private string paintName;
        private string paintDescription;
        private string customName;
        private string customDescription;
        private bool isNew;
        private uint position;
        private uint storageUnitContainedItemCount;
        private bool isStorageUnit;
        private bool isMusicKit;
        private ulong storageUnitId;
        private float paintSeed = -1;
        private float paintWear = -1;
        private uint statTrackCount;
        private string musickitName;
        private string musickitDescription;
        private string itemName;
        private string itemDescription;
        public decimal buff163Value;
        public decimal steamValue;
        private StatTrackType statTrackType;
        private DateTime tradableAfter;
        private bool isGrafitti;
        private Sticker[] stickers;
        public bool isSticker => itemName == "Sticker";
        public bool isStatTrack => !(statTrackType == StatTrackType.None);
        public string marketName
        {
            get
            {
                var result = "";
                result = itemName;
                if(IsStorageUnit) result = $"{customName} | {result}";
                if (result == "Music Kit") result = $"{result} | {musickitName}";
                if (result == "Sticker") result = $"{result} | {stickers[0].Name}";
                if (result == "Graffiti" || result == "Sealed Graffiti") result = $"{result} | {stickers[0].Name} ({sprayName})";
                if (isStatTrack) result = $"StatTrak\u2122 {result}";
                if (paintName != null) result = $"{result} | {paintName}";
                if (exterior != "N/A") result = $"{result} ({exterior})";
                return result;
            }
        }

        public string exterior
        {
            get
            {
                if (PaintWear == -1) return "N/A";
                if (PaintWear < 0.07f) return "Factory New";
                if (PaintWear < 0.15f) return "Minimal Wear";
                if (PaintWear < 0.38f) return "Field-Tested";
                if (PaintWear < 0.45f) return "Well-Worn";
                return "Battle-Scarred";
            }
        }

        public ulong Id { get => id; set => id = value; }
        public string PaintName { get => paintName; set => paintName = value; }
        public string PaintDescription { get => paintDescription; set => paintDescription = value; }
        public string CustomName { get => customName; set => customName = value; }
        public string CustomDescription { get => customDescription; set => customDescription = value; }
        public bool IsNew { get => isNew; set => isNew = value; }
        public uint Position { get => position; set => position = value; }
        public uint StorageUnitContainedItemCount { get => storageUnitContainedItemCount; set => storageUnitContainedItemCount = value; }
        public bool IsStorageUnit { get => isStorageUnit; set => isStorageUnit = value; }
        public ulong StorageUnitId { get => storageUnitId; set => storageUnitId = value; }
        public float PaintSeed { get => paintSeed; set => paintSeed = value; }
        public float PaintWear { get => paintWear; set => paintWear = value; }
        public uint StatTrackCount { get => statTrackCount; set => statTrackCount = value; }
        public string ItemName { get => itemName; set => itemName = value; }
        public string ItemDescription { get => itemDescription; set => itemDescription = value; }
        public StatTrackType StatTrakType { get => statTrackType; set => statTrackType = value; }
        public DateTime TradableAfter { get => tradableAfter; set => tradableAfter = value; }
        public Sticker[] Stickers { get => stickers; set => stickers = value; }
        public bool IsMusicKit { get => isMusicKit; set => isMusicKit = value; }
        public bool IsGrafitti { get => isGrafitti; set => isGrafitti = value; }
        public string SprayDescription { get => sprayDescription; set => sprayDescription = value; }
        public string SprayName { get => sprayName; set => sprayName = value; }
        public string MusickitDescription { get => musickitDescription; set => musickitDescription = value; }
        public string MusickitName { get => musickitName; set => musickitName = value; }

        public OutputCSGOItem ToOutputCSGOItem()
        {
                var result = new OutputCSGOItem();
                result.IsStorageUnit = IsStorageUnit;
                result.Quantity = 1;
                result.MarketName = marketName;
                result.Float = paintWear;
                result.SteamValue = steamValue;
                result.Buff163Value = buff163Value;
                if (result.MarketName.Contains("Sticker", StringComparison.OrdinalIgnoreCase)) result.IsSticker = true;
                if (result.MarketName.Contains("Case",StringComparison.OrdinalIgnoreCase)) result.IsCase = true;
                result.IsSpray = IsGrafitti;
                result.IsTradeable = DateTime.Now > TradableAfter;
                return result;
        }
    }

    public struct OutputCSGOItem
    {
        public int Quantity { get; set; }
        public string MarketName { get; set; }
        public decimal SteamValue { get; set; }
        public decimal Buff163Value { get; set; }
        public bool IsStorageUnit { get; set; }
        public float Float { get; set; }
        public bool IsCase { get; set; }
        public bool IsSpray { get; set; }
        public bool IsSticker { get; set; }
        public bool IsTradeable { get; set; }
    }
}
