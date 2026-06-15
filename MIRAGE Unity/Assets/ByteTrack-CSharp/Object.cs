namespace ByteTrackCSharp{
    public class Object
    {
        public Rect rect;
        public int label;
        public float prob;
        public int detection;

        public Object(Rect rect, int label, float prob, int detection)
        {
            this.rect = new Rect(rect);
            this.label = label;
            this.prob = prob;
            this.detection = detection;
        }
    }
}