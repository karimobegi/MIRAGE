namespace ByteTrackCSharp{
    public class LabelledSTrack : STrack{
        public int Label { get; protected set; }
        public int Detection { get; protected set;}
        public LabelledSTrack(Rect rect, float score, int label, int detection) : base(rect, score){
            Label = label;
            Detection = detection;
        }

        public override void update(STrack new_track, int frame_id)
        {
            if (new_track is LabelledSTrack labelledIncoming)
                {
                    this.Label = labelledIncoming.Label;
                    this.Detection = labelledIncoming.Detection;
                }

            base.update(new_track, frame_id);
        }

        public override void reActivate(STrack new_track, int frame_id, int new_track_id = -1)
        {
            if (new_track is LabelledSTrack labelledIncoming)
                {
                    this.Label = labelledIncoming.Label;
                    this.Detection = labelledIncoming.Detection;
                }
            base.reActivate(new_track, frame_id, new_track_id);
        }

    }
}