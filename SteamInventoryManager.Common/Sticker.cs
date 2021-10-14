namespace SteamInventoryManager.Common
{

    public struct Sticker
    {
        private int slot;
        private string name;
        private string description;
        private uint sticker_id;
        private float wear;
        public float wearPercentage => Wear * 100;

        public int Slot { get => slot; set => slot = value; }
        public string Description { get => description; set => description = value; }
        public string Name { get => name; set => name = value; }
        public uint Sticker_id { get => sticker_id; set => sticker_id = value; }
        public float Wear { get => wear; set => wear = value; }

        public float scale;
        public float rotation;
    }
}
