namespace ByteTrackCSharp{
    public class LabelledSTrack : STrack{
        public int Label { get; private set; }
        public LabelledSTrack(Rect rect, float score, int label) : base(rect, score){
            Label = label;
        }

    }
}