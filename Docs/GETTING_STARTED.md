# Set up MIRAGE for its first successful run

This guide is for the person preparing a computer for a researcher or operator. After this setup, give the operator the [short operator guide](OPERATOR_GUIDE.md).

MIRAGE is currently a Unity research project. This repository does not contain a ready-to-run Windows application, the three baseline ONNX models, or a demonstration video. A maintainer prepares those items once. A tutorial alone does not remove that preparation work.

The first goal is simple: play a short recorded street video and draw a visible outline around a detected person or car. Add removal, depth filtering, cameras, and VR after that works.

## 1. Prepare the computer and files

Have these items ready:

- Unity Hub and **Unity Editor 6000.2.2f1**, the version recorded in this project's settings.
- The repository folder, containing `MIRAGE Unity` and this guide.
- Exported **YOLO26s-seg**, **DA3-Small**, and **MI-GAN** ONNX models, prepared using [the model export instructions](../README.md#2-preparing-computational-models).
- A short video that you are permitted to use, with clearly visible people or vehicles. A 1280 × 720 street video is a convenient starting point.
- A computer with a working graphics driver. Performance depends on its GPU, model settings, and the effects selected.

An ONNX file is a model file that Unity can load. The operator should not have to export or configure it. Record the model filenames, versions, export options, and checksums when preparing a reusable model pack.

## 2. Open the Unity project

1. Open Unity Hub.
2. Add the **`MIRAGE Unity` folder** as a project. Choose the folder containing `Assets`, `Packages`, and `ProjectSettings`.
3. Open it with Unity 6000.2.2f1.
4. Wait for Unity to finish importing assets, resolving packages, and compiling scripts.
5. Open Unity's **Console** window. Resolve red compilation errors before continuing.
6. In **Edit > Project Settings > Player > Other Settings**, enable **Allow 'unsafe' Code** for the included tracking library.

Do not change editor or package versions during the first setup. A maintainer can validate upgrades separately and record the resulting configuration.

**Checkpoint:** Unity has finished importing, and the Console contains no unresolved red compilation errors.

## 3. Open the desktop scene

1. Choose **MIRAGE > Prepare desktop > 1. Open Desktop UI**. You can also open `Assets/Resources/Scenes/Pipeline/Desktop UI.unity` in the Project window.
2. Keep this scene at that path when using the dedicated Windows package builder. It builds this scene explicitly. Separate scene copies can be used for experiments, but the package command will ask you to return to Desktop UI.
3. Find the **Hierarchy** window. It lists the objects in the scene.
4. Select **Pipeline**, **CameraInput**, and the objects under **Models** in turn to locate their settings in the **Inspector** window.

Use **Desktop UI** for this guide. The **Desktop** scene expects manual effect setup. VR needs a separately prepared headset and calibrated projection setup.

## 4. Import and assign the models

1. Choose **MIRAGE > Prepare desktop > 2. Apply desktop defaults**. This assigns model roles and the dimensions in the table below, selects MI-GAN, and starts with depth/removal effects off. It does not download or check model weights.
2. Create an asset folder for your model pack in the Project window.
3. Copy the three exported ONNX files into it and wait for Unity to import them.
4. Select the **YOLO** object under **Models**. Drag the YOLO26 segmentation asset into its **Model Asset** field.
5. Select the **DepthAnything** object. Assign the DA3-Small asset.
6. Select **MI-GAN**. Assign the MI-GAN asset exported with this repository's modified export script.
7. Select **Pipeline** and verify its assigned model runners. Use MI-GAN as the initial inpainting backend.
8. Save the scene, then choose **MIRAGE > Prepare desktop > 3. Check setup**. Follow the reported recovery instructions.

Some saved scenes and prefabs contain historical model references and dimensions. Assigning a new file does not make those values correct automatically. For the documented exports, a useful starting configuration is:

| Inspector setting | YOLO26s-seg | DA3-Small exported at 504 | MI-GAN |
|---|---:|---:|---:|
| Input Width × Input Height | 640 × 640 | 504 × 504 | 512 × 512 |
| Output Width × Output Height | 640 × 360 | 640 × 360 | 1280 × 720 |
| Image Width × Image Height | 1280 × 720 | 1280 × 720 | 1280 × 720 |
| Max Objects | 256 | 256 | 256 |
| Update Rate | 1 | 1 | 1 |

These values describe different things. **Input** dimensions must match the exported model. YOLO and depth **output** dimensions must match each other because they share an object-pixel grid. MI-GAN's model still processes 512 × 512 crops, while its full-image output can match the 1280 × 720 presentation image. Keep the other scene references to output images and postprocessors intact.

For DA3-Small, keep **Output Units** set to **Relative**, **Depth Scale** at `1`, **Depth Offset** at `0`, and **Normalize Input** enabled for the documented exporter. Relative depth values are not metres.

**Checkpoint:** each enabled model has an assigned Model Asset, its input dimensions match its export, and the YOLO/depth output dimensions agree.

## 5. Choose how the demonstration video opens

The desktop **Help & setup (F1)** window now lets an operator open a local recording or choose a connected camera while MIRAGE runs. A maintainer can also prepare an imported clip in the scene so a familiar picture is available immediately after launch.

To prepare an imported clip:

1. Import the video into a folder in Unity's Project window.
2. Select **CameraInput** in the Hierarchy.
3. Enable **Use Debug Video Input**. Here, “debug video” means a recorded video source.
4. Set **Requested Width** to `1280` and **Requested Height** to `720` for this demonstration configuration.
5. Select the **VideoPlayer** child object.
6. Choose **Video Clip** as its source if necessary, then assign the imported clip to its **Video Clip** field.
7. Enable looping if you want the clip to repeat during the demonstration.
8. Save the scene.

To choose a recording after entering Play mode, click **Help & setup (F1) > Input > Choose video...**, then select the local file. If a file dialog is unavailable, paste the full local file path into the text field and click **Open recording**. This path does not require importing the video into the Unity project. File decoding still depends on the computer's supported video formats.

## 6. Check the plain video

1. Click Unity's triangular **Play** button.
2. Click the **Game** window to give it keyboard and mouse focus.
3. Wait for the models to initialize and for the first processed image to appear.
4. If no source was prepared, open **Help & setup (F1) > Input** and choose a recording.
5. Confirm that the video moves and that the picture has the intended proportions.
6. Open **Status & support** in the help window. Follow any **Needs attention** message and its recovery instruction before continuing. Waiting for a first frame is different from a missing model.

The runtime help window's **Input** tab provides **Pause / resume**, **Restart recording**, and **Stop input**. In the Unity Editor, the VideoPlayer Inspector additionally provides a timeline for seeking. Close help before interacting with the effect panel underneath it.

**Checkpoint:** a moving picture is visible. If the picture is black, follow the troubleshooting table below before adding effects.

## 7. Make the first effect visible

For the shortest first test, open **Help & setup (F1) > Start here**, click **Load Outline people**, and then **Close help and return to MIRAGE**. Use a clip with a visible person. This loads the supplied reusable configuration. If the effects panel still covers the picture, focus the picture and press Esc once. The following manual exercise teaches the same controls with a chosen object type.

1. Focus the Game window. If the settings panel is hidden, press **Esc** once to show it.
2. Under **Object Type**, select **Person** if the clip contains a visible pedestrian. Select **Car** for a clip with cars or trucks.
3. Find the resulting class card and click its **+** button.
4. Choose **Outline**.
5. Leave the effect's range at its full `0–100` starting range for this first test.
6. Use the **Color** control to choose a bright color with visible opacity.
7. Under **Layers**, leave **Base** and **Augmented Reality** enabled.
8. Press **Esc** to apply the settings and hide the panel.
9. Wait for the next processed frame.

**Success:** the original video remains visible and detected objects of the chosen type receive an outline. Detection can miss an object; an outline is not expected on every object in every frame.

Open the panel with Esc, remove the effect using its close button or clear the class selection, and press Esc again to apply. Confirm the outline disappears. This gives the operator a simple way to check both adding and removing an effect.

To keep a useful configuration, open **Help & setup (F1) > Presets**, enter a descriptive name, and click **Save preset**. Use **Load: name** to restore a saved setup. Saving an existing name replaces that preset; use a new name to preserve the earlier version. MIRAGE also remembers the last successfully applied configuration by default. See the operator guide's [preset instructions](OPERATOR_GUIDE.md#save-and-reuse-a-setup) for sharing and replacement behavior.

## 8. Try removal only after outlines work

1. Reopen the panel with Esc.
2. Choose a class and add **Remove** from its **+** menu.
3. Leave the full starting range selected.
4. Enable the **Diminished Reality** layer.
5. Apply with Esc.

MI-GAN fills the selected region with a plausible background. It can leave visible seams or invent incorrect details. Begin with one object type and one effect so changes are easy to identify. The maintainer can adjust mask dilation and feathering after the baseline works.

Moebius is an optional, slower experiment. Its local setup and backend switch are described in [the Moebius guide](../Models/Moebius/README.md). It is not required for ordinary MI-GAN use.

## 9. Prepare a camera source, if required

The operator can use **Help & setup (F1) > Input**, click **Refresh cameras**, select a listed device, and click **Use selected camera**. This switches the running source and clears the old input's temporal history.

To prepare a default camera in the scene instead:

Stop Unity Play mode before changing the prepared input configuration.

1. Connect the intended camera and allow the operating system to access it.
2. Select **CameraInput**.
3. Click **Refresh Camera List**, then choose the device under **Select Camera**.
4. Disable **Use Debug Video Input** and enable **Start On Awake**.
5. Set the requested resolution and frame rate for your setup.
6. Save the scene and enter Play mode again.
7. Confirm a live image before applying effects.

The driver can choose a different actual resolution. MIRAGE waits for the camera's first real frame before allocating its capture image. Check proportions and output alignment again when changing devices.

## Troubleshooting

| What you see | What to do |
|---|---|
| Unity has red compilation errors | Stop setup and resolve the first compilation error. Package/import errors need a maintainer; changing effect controls will not fix them. |
| “Assign an ONNX Model Asset” | Stop Play mode, select the named model object, and assign the prepared asset in Model Asset. |
| A depth/segmentation output-dimension error | Set their Output Width and Output Height to identical values, then restart Play mode. |
| Black recorded-video image | Open F1 > Input and choose or restart a recording. For an imported scene clip, confirm Use Debug Video Input, the VideoPlayer reference, and its Video Clip assignment. |
| No cameras found | Reconnect the device, check operating-system camera access, close other applications using it, and click Refresh Camera List again. |
| Moving video but no outline | Choose a class actually present in the clip; check that Outline exists, the Augmented Reality layer is enabled, the color is visible, the range is full, and Esc has applied the change. |
| No removal | Confirm MI-GAN is selected and prepared, Remove is assigned to the intended class, the Diminished Reality layer is enabled, and settings were applied. |
| Controls do not respond to Esc | Click the Game window, then press Esc once. The Scene window and Inspector have their own keyboard focus. |
| Labels say “relative” | This is expected for DA3-Small. Use the full range for a first test; choose a metric model or independently validated calibration when metre-based ranges are needed. |
| The picture updates very slowly | Restore MI-GAN if Moebius is selected. Use one effect/class, Update Rate 1, Maximum Crops Per Frame 1, and leave experimental temporal reuse off while diagnosing. |
| Moebius connection error | Start the local service using the Moebius guide, or use the editor's MI-GAN backend switch. |

For a reproducible support report, use **Help & setup (F1) > Status & support > Export support ZIP**. It creates a local diagnostic archive and opens its folder. Review its contents before sharing; no automatic upload occurs. **Open logs folder** opens the current session's underlying files. The [logging guide](LOGGING.md) explains their contents, retention, and timing definitions.

## Hand the setup to an operator

Before handing over the computer, run the outline test from beginning to end, save the prepared scene, identify the exact input clip/camera, and provide a known starting configuration. Give the operator the [operator guide](OPERATOR_GUIDE.md), the name of the prepared project or tested application shortcut, and one support contact.

To create the supplied Windows package, stop Play mode, save the prepared Desktop UI scene, and choose **MIRAGE > Build > Windows desktop package**. Select a destination outside `Assets`. On a successful build, the output includes `MIRAGE.exe`, `START-HERE.txt`, and the guides alongside Unity's required data and libraries. Test that complete folder outside the Editor before handing it over; this build path has not been executed in the current source-only validation environment.

For distributing a Windows application, use the [release checklist](RELEASE_CHECKLIST.md). A prepared developer checkout and a tested operator release are separate handoff artifacts.
