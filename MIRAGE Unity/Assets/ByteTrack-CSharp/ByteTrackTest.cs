using UnityEngine;
using System.Collections.Generic;

namespace ByteTrackCSharp{
public class ByteTrackTest : MonoBehaviour
{
    void Start()
    {
        var tracker = new BYTETracker(obj => new LabelledSTrack(obj.rect, obj.prob, obj.label, obj.detection));
        
        var fakeDetections = new List<Object> {
            new Object(new Rect(100, 100, 50, 80), 2, 0.9f, 1),new Object(new Rect(200, 1500, 50, 80), 2, 0.7f, 2)
        };
        
        var tracks = tracker.update(fakeDetections);
        Debug.Log($"Tracks returned: {tracks.Count}");
        foreach (var t in tracks)
        {
            Debug.Log($"TrackID: {t.TrackId}, Rect: {t.getRect()}");
        }
        
        // Run a second frame to verify IDs persist
        var tracks2 = tracker.update(fakeDetections);
        Debug.Log($"Frame 2 - Tracks returned: {tracks2.Count}");
        foreach (var t in tracks2)
        {
            Debug.Log($"TrackID: {t.TrackId}, Rect: {t.getRect()}");
        }
    }
}}