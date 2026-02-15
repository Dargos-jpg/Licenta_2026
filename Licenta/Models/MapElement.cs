namespace WifiSignalOptimizer.Models
{
    public enum ElementType { Router, Fridge, Microwave, ConcreteWall }

    public class MapElement
    {
        public ElementType Type { get; set; }
        public double X { get; set; }
        public double Y { get; set; }

        // Cât de mult strică semnalul acest obiect?
        public int SignalPenalty
        {
            get
            {
                return Type switch
                {
                    ElementType.Router => 0,       // Sursa
                    ElementType.Fridge => 15,      // Metal masiv (-15dBm)
                    ElementType.Microwave => 10,   // Interferențe
                    ElementType.ConcreteWall => 12,// Beton
                    _ => 0
                };
            }
        }
    }
}