namespace WifiSignalOptimizer.Models
{
    public enum ElementType
    {
        Router,
        Fridge,
        Microwave,
        ConcreteWall,
        BrickWall,   // ✅ NOU
        WoodDoor,    // ✅ NOU
        TV           // ✅ NOU
    }

    public class MapElement
    {
        public ElementType Type { get; set; }
        public double X { get; set; }
        public double Y { get; set; }

        /// <summary>
        /// Atenuarea semnalului WiFi cauzată de acest obstacol (în dBm).
        /// </summary>
        public int SignalPenalty
        {
            get
            {
                return Type switch
                {
                    ElementType.Router => 0,   // Sursa — nu atenuează
                    ElementType.ConcreteWall => 12,  // Beton armat
                    ElementType.BrickWall => 6,   // Cărămidă
                    ElementType.WoodDoor => 3,   // Ușă lemn
                    ElementType.Fridge => 15,  // Metal masiv
                    ElementType.Microwave => 8,   // Interferențe + metal
                    ElementType.TV => 5,   // Metal + electronice
                    _ => 0
                };
            }
        }
    }
}