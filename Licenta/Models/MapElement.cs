namespace WifiSignalOptimizer.Models
{
    public enum ElementType
    {
        Router,
        Fridge,
        Microwave,
        ConcreteWall,
        BrickWall,
        WoodDoor,
        TV
    }

    public class MapElement
    {
        public ElementType Type { get; set; }
        public double X { get; set; }
        public double Y { get; set; }

        /// <summary>
        /// the WiFi signal attenuation caused by this obstacle (in dBm).
        /// </summary>
        public int SignalPenalty
        {
            get
            {
                return Type switch
                {
                    ElementType.Router => 0,   // source, does not attenuate
                    ElementType.ConcreteWall => 12,  // reinforced concrete
                    ElementType.BrickWall => 6,   // brick
                    ElementType.WoodDoor => 3,   // wood door
                    ElementType.Fridge => 15,  // dense metal
                    ElementType.Microwave => 8,   // interference + metal
                    ElementType.TV => 5,   // metal + electronics
                    _ => 0
                };
            }
        }
    }
}