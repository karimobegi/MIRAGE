# Run a prepared MIRAGE session

Use this guide after a maintainer has prepared the computer, models, input, and starting settings. You do not need to export models or edit code.

Ask the maintainer to identify these four items before your first session:

| Item | What you need |
|---|---|
| Start | The prepared project or tested application shortcut |
| Picture | The name of the video or camera you should see |
| Starting settings | The approved class/effect configuration for this session |
| Support | The person to contact when the checks below fail |

This repository currently supplies a Unity project. Use an application shortcut only if a maintainer has separately built and tested that application; there is no packaged installer included in the repository.

## Start and check the picture

1. Open the prepared project or application using the maintainer's instructions.
2. In the Unity Editor, open the prepared **Desktop UI** scene if it is not already open and click the triangular **Play** button. A tested application should start its prepared scene itself.
3. Click the picture area. In Unity this is the **Game** window.
4. The **Help & setup** window opens on a first desktop run. You can open it again with the **Help & setup (F1)** button or the F1 key.
5. Choose the **Input** tab. Click **Choose video...**, select the recording supplied for the session, and wait for its first picture. If needed, paste its full local path and click **Open recording**.
6. For a camera session, click **Refresh cameras**, select the intended device, then click **Use selected camera**.
7. Confirm you recognize the source and its picture proportions look correct.
8. Open **Status & support**. If a message starts with **Needs attention**, follow the instruction below it. A missing model or invalid size needs the maintainer.

If there is no picture, stop here and use the troubleshooting table. Effects cannot repair a missing video or camera source.

Use **Pause / resume** or **Restart recording** in the Input tab to repeat a moment. **Stop input** stops the current source. Choose a recording or camera again to continue. Your selected input is separate from an effect preset.

Recordings keep their proportions; black borders can appear when their shape differs from the prepared display. If Status & support reports a camera aspect mismatch, use the maintainer's configured camera mode or ask them to prepare matching image dimensions.

## First practice: outline a person or car

For a one-button start, choose **Start here > Load Outline people** in the help window, then click **Close help and return to MIRAGE**. If the effects panel still covers the picture, click the picture and press Esc once. A visible person in the picture should receive an outline. To learn the effect controls or select cars, use the manual steps below.

1. Close the help window. Click the picture and press **Esc** once if the effects settings panel is hidden.
2. Under **Object Type**, select **Person**, or select **Car** if that is the clearly visible object in your video.
3. On its class card, click **+**.
4. Choose **Outline**.
5. Keep the full starting range, `0–100`, for this practice.
6. Set a bright, visible color with the **Color** control.
7. Keep the **Base** and **Augmented Reality** layers enabled.
8. Press **Esc** to apply and hide the settings.

You should see outlines around detected objects of that type. A missed object does not necessarily mean the application has stopped. Check several frames with a clearly visible object.

To undo the practice, press Esc, close the Outline effect card or clear that object type, and press Esc again.

## What the controls mean

| Control | Plain-language meaning |
|---|---|
| Object Type | Which detected objects an effect should change: Person, Car, Bus, Traffic Lights, Sign, Train, or Animal. The Car category also includes trucks in the supplied setup. |
| + on a class card | Add an effect to that object type. |
| Outline | Draw a line around the detected object. |
| BBox | Draw a rectangle around the detected object. |
| Icon | Show the configured object icon. |
| Info | Show the object's class and available depth estimate. |
| Color Mask | Put a color over the selected object. |
| Blur | Blur the selected object. |
| Painted | Apply the painterly filter. |
| Remove | Replace the selected object region with a generated background. |
| Opacity | Show some of the original object over a generated background. |
| Color | Choose an effect's visible color; opacity affects visibility. |
| Range | Limit an effect using the selected depth model's values. Use the maintainer's settings. DA3-Small values are relative, not metres. |
| Base layer | Show the original picture. |
| Augmented Reality layer | Show overlays such as outlines, labels, and boxes. |
| Diminished Reality layer | Show the configured removal/modified-image layers. |
| Esc | Show the settings, or apply changes and hide them. Click the picture first if it does not respond. |
| Help & setup (F1) | Open source selection, reusable presets, startup checks, and support tools. Close help to interact with the effects panel. |

Some controls depend on the effect or scene. The supplied Desktop UI exposes the named effects above; additional effects described in the project README need maintainer setup.

## Save and reuse a setup

1. Create the effect setup you want and apply it with Esc.
2. Open **Help & setup (F1)** and choose **Presets**.
3. Type a descriptive name, such as `People yellow outline`.
4. Click **Save preset** and check the success message.
5. Use **Load: name** to restore a saved preset. Loading replaces the current effect setup.

Saving with an existing name replaces that named preset. Use a new name to retain the earlier version. Starter presets are **Outline people**, **Blur people**, and **Remove people**; start with Outline people while checking a new installation.

By default, MIRAGE restores the last successfully applied setup when it starts again. Edits still open in the effects panel are not remembered until you apply or save them. Named presets provide clear, shareable starting points even when the last session used different settings.

A preset includes object/effect selections, colors, ranges, layers, and model update settings. It does not contain a video, camera selection, model files, or participant data. Choose the input separately on each prepared computer. Presets that use depth filtering record their depth units and model name; a mismatch is rejected before the current setup changes. Ask the maintainer to prepare matching models and units instead of changing a preset file by hand.

Use **Open preset folder** to find the preset's JSON file. To share it, copy that file into the preset folder on the other prepared computer and reopen the Presets tab. The other computer must have the required models and scene setup. Keep the original file if you want to retain an earlier configuration.

## Keep a session understandable

Make one change at a time. Add one effect to one object type, apply it, and inspect the picture before adding another effect. Use the maintainer's starting settings for comparisons between sessions.

The picture can remain still while MIRAGE computes a new result. Moebius is an optional, slower mode; ask the maintainer to select MI-GAN for ordinary interactive demonstrations. Generated backgrounds may contain visible mistakes.

An **Info** label ending in **(relative)** is expected with the supplied DA3-Small depth model. Treat it as a model value. It is not a measured distance in metres.

## When something goes wrong

| Problem | First action |
|---|---|
| The settings panel is missing | Close Help & setup, click the picture, and press Esc once. |
| The video moves but an effect is missing | Open the panel, check the object type, effect card and matching layer, then apply with Esc. |
| Nothing is visible | Confirm the Base layer is enabled. If the source is still blank, contact the maintainer. |
| Removal looks incorrect | Remove the Remove effect, apply with Esc, and record which clip/camera and object type produced it. |
| The picture stops or becomes very slow | Record what changed immediately before the problem. If possible, restore the starting configuration; otherwise stop the session and contact the maintainer. |
| A message names a missing model, dimensions, package, or connection | Open F1 > Status & support and follow its recovery message. Give unresolved setup messages to the maintainer. You do not need to change model files or numeric dimensions yourself. |
| A preset reports incompatible depth units/model or an invalid file | Keep the current setup, load an approved compatible preset, or use Outline people for a basic test. Give the rejected preset and message to the maintainer. |

To report a problem:

1. Note the source name, selected object/effect, approximate time, and what you expected to see.
2. Open **Help & setup (F1) > Status & support**.
3. Click **Export support ZIP**. Wait for the success message and the folder to open.
4. Review the ZIP, then send it to the maintainer using your usual support channel.

The ZIP contains diagnostic metadata and logs. It does not include video frames, recordings, audio, or study settings, and nothing is uploaded automatically. Logs can contain text from error messages, so review the archive before sharing it.

**Open logs folder** opens the current session's files. **Refresh setup checks** updates the checks shown in the window. Processed updates per second describe MIRAGE's output updates; a large **Age of displayed frame** can help explain an old or frozen picture.

## Finish

1. Return to the agreed starting settings or save the intended reusable configuration under Presets.
2. Open **Help & setup (F1)** and click **Quit MIRAGE**. In Unity this leaves Play mode; in the prepared application it closes MIRAGE. The Unity Play button or the application's normal window close control can also be used.
3. Tell the maintainer about any error or unexpected visual result encountered during the session.

Stopping Unity Play mode discards ordinary runtime scene edits. Saved preset files remain in your user-data folder. Confirm which named preset the next operator should use.
