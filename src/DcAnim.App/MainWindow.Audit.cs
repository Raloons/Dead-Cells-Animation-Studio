using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using DcAnim.Core.Catalog;
using DcAnim.Core.Export;
using DcAnim.Core.Model;
using DcAnim.Core.Playback;
using DcAnim.Core.Sequence;

namespace DcAnim.App;

/// <summary>
/// Headless workflow audit (--audit &lt;dir&gt;). Drives the same methods the
/// buttons, menus, timeline and inspector invoke, in realistic and adversarial
/// orders, catching the first failure per step with a full stack trace.
/// Dialogs that require a human (file pickers, confirm prompts) are excluded
/// here and covered by direct-logic tests + manual verification instead.
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<string> _auditLog = new();
    private int _auditPass;
    private int _auditFail;
    private string _auditDir = string.Empty;
    private bool _auditMode;

    private void AuditLog(string line)
    {
        _auditLog.Add(line);
        try { File.AppendAllText(Path.Combine(_auditDir, "AUDIT_LOG.txt"), line + Environment.NewLine); }
        catch { }
    }

    private void AuditStep(string name, Action body)
    {
        AuditLog($"--- RUN {name}");
        try
        {
            body();
            _auditPass++;
            AuditLog($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _auditFail++;
            AuditLog($"[FAIL] {name} :: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
        }
    }

    private async Task AuditStepAsync(string name, Func<Task> body)
    {
        AuditLog($"--- RUN {name}");
        try
        {
            await body();
            _auditPass++;
            AuditLog($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _auditFail++;
            AuditLog($"[FAIL] {name} :: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
        }
    }

    private void AuditShot(string name)
    {
        try
        {
            // Force a full layout pass first: offscreen captures otherwise
            // risk showing templated controls (numerics, checkboxes) and
            // text one frame behind the model in headless runs.
            UpdateLayout();
            var pixelSize = new PixelSize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
            using var rtb = new RenderTargetBitmap(pixelSize, new Avalonia.Vector(96, 96));
            rtb.Render(this);
            rtb.Save(Path.Combine(_auditDir, name + ".png"));
            AuditLog($"[SHOT] {name}.png");
        }
        catch (Exception ex)
        {
            AuditLog($"[SHOT-FAIL] {name}: {ex.Message}");
        }
    }

    private static void AuditCheck(bool cond, string what)
    {
        if (!cond)
            throw new InvalidOperationException("check failed: " + what);
    }

    private void AssertCanTransportFalse() =>
        AuditCheck(!CanTransport, "transport guarded while empty");

    private void AuditInvariants(string where)
    {
        // Engine position always valid.
        int idx = _engine.CurrentFrameIndex;
        int count = ActiveFrameCount;
        if (count == 0)
            AuditCheck(idx == -1, $"{where}: empty clip must report index -1 (got {idx})");
        else
            AuditCheck(idx >= 0 && idx < count, $"{where}: index {idx} in [0,{count})");
        // Bitmap cache matches composed frames 1:1.
        AuditCheck(_seqBitmaps.Count == (_seqClip?.FrameCount ?? 0),
            $"{where}: bitmap cache {_seqBitmaps.Count} vs clip {_seqClip?.FrameCount ?? 0}");
        // Composed artifacts agree with resolves.
        if (_seqRendered is not null && _seqClip is not null)
            AuditCheck(_seqRendered.Frames.Count == _seqClip.FrameCount,
                $"{where}: rendered frames match clip");
        // Selection refers to something that exists (or nothing).
        if (_seqTarget == SeqTargetKind.Clip && _seqTargetId.Length > 0)
            AuditCheck(_doc.Project.Clips.Any(c => c.Id == _seqTargetId), $"{where}: stale clip selection");
        if (_seqTarget == SeqTargetKind.Effect && _seqTargetId.Length > 0)
            AuditCheck(_doc.Project.Effects.Any(c => c.Id == _seqTargetId), $"{where}: stale effect selection");
        if (_seqTarget == SeqTargetKind.Audio && _seqTargetId.Length > 0)
            AuditCheck(_doc.Project.Audio.Any(c => c.Id == _seqTargetId), $"{where}: stale audio selection");
        if (_seqSelectedClip is not null)
            AuditCheck(_seqResolved.Any(r => r.Clip.Id == _seqSelectedClip), $"{where}: stale selected clip");
    }

    private void AuditTick(int frames = 5)
    {
        for (int i = 0; i < frames; i++)
        {
            _engine.Tick(TimeSpan.FromSeconds(1.0 / Math.Max(1.0, _doc.Project.Fps)));
            RefreshFrame();
        }
    }

    public async Task RunAuditAsync(string dir)
    {
        _auditMode = true;
        _auditDir = dir;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "AUDIT_LOG.txt"),
            $"AS-M4 audit started {DateTime.Now:O}{Environment.NewLine}");
        _auditLog.Clear();
        _auditPass = 0;
        _auditFail = 0;

        bool hasCatalog = _loader is not null && _loader.Catalog.Contains("AtkKatanaB2");
        if (!hasCatalog)
        {
            AuditLog("[ABORT] no game catalog; res.pak required");
            await FinishAuditAsync();
            return;
        }

        string A() => "AtkKatanaB2";
        string B() => _loader!.Catalog.Contains("AtkKatanaB4") ? "AtkKatanaB4" : "AtkKatanaB2";
        string fxPath = Path.Combine(dir, "shot.png");
        string wavPath = Path.Combine(dir, "tone.wav");

        // Local fixtures: a real transparent PNG and a real 1s 880Hz WAV so the
        // happy paths (decode, waveform, voice lifecycle) actually execute.
        string fixtureFx = Path.Combine(dir, "shot.png");
        string fixtureWav = Path.Combine(dir, "tone.wav");
        AuditStep("fixtures.generate", () =>
        {
            using var bmp = new SkiaSharp.SKBitmap(96, 96, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
            using (var canvas = new SkiaSharp.SKCanvas(bmp))
            {
                canvas.Clear(SkiaSharp.SKColors.Transparent);
                using var paint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(255, 128, 64, 255) };
                canvas.DrawCircle(48, 48, 30, paint);
            }
            using (var img = SkiaSharp.SKImage.FromBitmap(bmp))
            using (var d = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(fixtureFx, d.ToArray());
            const int rate = 44100;
            using var stream = File.Create(fixtureWav);
            using var writer = new System.IO.BinaryWriter(stream);
            short[] pcm = new short[rate];
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = (short)(Math.Sin(2 * Math.PI * 880 * i / rate) * 9000);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + pcm.Length * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(pcm.Length * 2);
            foreach (short s in pcm) writer.Write(s);
            AuditCheck(new FileInfo(fixtureFx).Length > 0 && new FileInfo(fixtureWav).Length > 44, "fixtures written");
        });

        // ---------- 1. Startup / viewer states ----------
        AuditStep("viewer.select+play+pause", () =>
        {
            Browser.Select("AtkKatanaB2");
            TogglePlay(); TogglePlay();
            AuditInvariants("viewer");
        });
        AuditStep("viewer.step+seek+scrub", () =>
        {
            _engine.StepNext(); _engine.StepNext(); _engine.StepPrevious();
            _engine.SeekFrame(0);
            _engine.Scrub(0); _engine.Scrub(0.5); _engine.Scrub(1);
            _engine.Scrub(-0.5); _engine.Scrub(1.5); // clamped, must not throw
            RefreshFrame();
            AuditInvariants("viewer-scrub");
        });
        AuditStep("viewer.loop+fps+speed", () =>
        {
            _engine.Loop = false; _engine.Loop = true;
            Transport.FpsBox.SelectedItem = Transport.FpsItems.First(i => i.Tag?.ToString() == "12");
            Transport.FpsBox.SelectedItem = Transport.FpsItems.First(i => i.Tag?.ToString() == "60");
            _engine.Speed = 0.25; _engine.Speed = 4; _engine.Speed = 1;
            RefreshFrame();
        });
        AuditStep("viewer.search-no-results+clear", () =>
        {
            Browser.Search("zzz-no-such-animation");
            AuditCheck(true, "noop");
            Browser.Search("");
        });
        AuditStep("viewer.category-switch-with-selection", () =>
        {
            // Regression: selecting an Attacks animation, then switching to
            // the Idle pill crashed in ApplyFilter (Avalonia selection model
            // tripped on the stale index). Exercises every category with a
            // live selection, in both list and grid modes.
            Browser.SetViewMode(grid: false);
            Browser.Select("AtkKatanaB2");
            Browser.SetCategory(AnimationCategory.Idle);
            Browser.SetCategory(AnimationCategory.Movement);
            Browser.SetCategory(AnimationCategory.Other);
            Browser.SetCategory(AnimationCategory.Attacks);
            Browser.SetCategory(null); // All
            Browser.SetViewMode(grid: true);
            Browser.Select("AtkKatanaB2");
            Browser.SetCategory(AnimationCategory.Idle);
            Browser.SetCategory(null);
            Browser.SetViewMode(grid: false);
            AuditCheck(Browser.SelectedId == "AtkKatanaB2", "selection preserved across filters");
        });
        AuditStep("viewer.list-grid-toggle", () =>
        {
            Browser.SetViewMode(grid: true);
            Browser.SetViewMode(grid: false);
        });
        AuditStep("viewer.mode-toggle-no-project", () =>
        {
            SetSequenceMode(true);   // empty project
            SetSequenceMode(false);
            SetSequenceMode(true);
            AuditInvariants("empty-seq");
        });
        AuditStep("viewer.empty-sequence-transport", () =>
        {
            // UI entry points are guarded: toggling/playing an empty
            // sequence is a silent no-op.
            TogglePlay();
            AssertCanTransportFalse();
            AuditInvariants("empty-transport");
        });
        AuditStep("engine.empty-clip-contract", () =>
        {
            // The engine itself rejects empty clips; all UI call sites guard
            // via CanTransport. Assert the contract (not a crash path).
            bool threwStep = false, threwSeek = false, threwLoad = false;
            try { new PlaybackEngine().StepNext(); } catch (InvalidOperationException) { threwStep = true; }
            try { new PlaybackEngine().SeekFrame(0); } catch (InvalidOperationException) { threwSeek = true; }
            try
            {
                new PlaybackEngine().Load(new AnimationClip
                {
                    Id = "x", Frames = new List<ClipFrame>(),
                    SourceDescription = "t", DefaultFps = 30,
                });
            }
            catch (ArgumentException) { threwLoad = true; }
            AuditCheck(threwStep && threwSeek && threwLoad, "engine guards empty clips");
        });
        AuditShot("audit-01-empty-sequence");

        // ---------- 2. Undo/redo on empty history ----------
        AuditStep("undo-redo-empty", () =>
        {
            Undo(); Redo(); Undo(); Redo();
            AuditCheck(!_doc.History.CanUndo, "empty history stays empty");
            AuditInvariants("empty-undo");
        });

        // ---------- 3. Fresh project, empty ops ----------
        AuditStep("new-project-empty", () =>
        {
            _doc.New("Audit Empty");
            _seqSelectedClip = null;
            ClearSeqTarget();
            SetSequenceMode(true);
            RebuildSequence();
            AuditCheck(_seqResolved.Count == 0, "no resolved clips");
            AuditInvariants("new-empty");
        });

        // ---------- 4. Clips ----------
        AuditStep("clip.add-first+select+play", () =>
        {
            var c = _doc.AddClip(A(), 1.0);
            _seqSelectedClip = c.Id;
            SelectSeqTarget(SeqTargetKind.Clip, c.Id);
            RebuildSequence();
            TogglePlay(); AuditTick(3); TogglePlay();
            AuditInvariants("one-clip");
        });
        AuditStep("clip.add-more+move+duplicate", () =>
        {
            var b = _doc.AddClip(B(), 1.25);
            _doc.AddClip("AtkKatanaA", 0.5, 0);
            _doc.MoveClip(b.Id, 0);
            _doc.DuplicateClip(b.Id);
            RebuildSequence();
            AuditCheck(_seqResolved.Count == 4, "4 resolved clips");
            AuditInvariants("multi-clip");
        });
        AuditStep("clip.speeds+fps", () =>
        {
            _doc.SetFps(60);
            _doc.SetFps(24);
            _doc.SetClipSpeed(_doc.Project.Clips[0].Id, 0.25);
            _doc.SetClipSpeed(_doc.Project.Clips[0].Id, 4.0);
            _doc.SetClipSpeed(_doc.Project.Clips[0].Id, 1.0);
            _doc.SetFps(30);
            RebuildSequence();
            AuditInvariants("speeds");
        });
        AuditStep("clip.seek-boundaries", () =>
        {
            int n = _seqClip?.FrameCount ?? 0;
            _engine.SeekFrame(0);
            _engine.SeekFrame(Math.Max(0, n - 1));
            _engine.Scrub(1.0);
            _engine.Scrub(0.0);
            RefreshFrame();
            AuditInvariants("seek-edges");
        });
        AuditStep("clip.delete-middle+first+last", () =>
        {
            var ids = _doc.Project.Clips.Select(c => c.Id).ToList();
            _doc.RemoveClip(ids[1]);
            RebuildSequence();
            _doc.RemoveClip(_doc.Project.Clips[0].Id);
            RebuildSequence();
            _doc.RemoveClip(_doc.Project.Clips[^1].Id);
            RebuildSequence();
            AuditInvariants("after-deletes");
        });
        AuditStep("clip.delete-only-clip+undo", () =>
        {
            while (_doc.Project.Clips.Count > 1)
                _doc.RemoveClip(_doc.Project.Clips[^1].Id);
            var only = _doc.Project.Clips[0].Id;
            _seqSelectedClip = only;
            SelectSeqTarget(SeqTargetKind.Clip, only);
            _doc.RemoveClip(only);
            RebuildSequence();
            AuditCheck(_seqClip?.FrameCount == 0, "empty after deleting only clip");
            if (!_doc.Undo())
                throw new InvalidOperationException("undo failed");
            RebuildSequence();
            AuditCheck(_seqClip?.FrameCount > 0, "clip restored by undo");
            AuditInvariants("undo-delete-only");
        });
        AuditShot("audit-02-populated");

        // ---------- 5. Transforms ----------
        AuditStep("transform.set-all+extremes", () =>
        {
            var id = _doc.Project.Clips[0].Id;
            _doc.SetClipTransform(id, Transform.Identity);
            _doc.SetClipTransform(id, new Transform
            {
                X = -5000, Y = 5000, ScaleX = 0.01, ScaleY = 16,
                RotationDegrees = 720, Opacity = 0, PivotX = 0, PivotY = 1,
                FlipH = true, FlipV = true,
            });
            RebuildSequence();
            _doc.SetClipTransform(id, Transform.Identity);
            RebuildSequence();
            AuditInvariants("transform-extremes");
        });
        AuditStep("transform.invalid-rejected", () =>
        {
            var id = _doc.Project.Clips[0].Id;
            bool threw = false;
            try { _doc.SetClipTransform(id, Transform.Identity with { Opacity = double.NaN }); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            AuditCheck(threw, "NaN opacity rejected");
        });

        // ---------- 6. Keyframes ----------
        AuditStep("keyframe.crud+navigate", () =>
        {
            var id = _doc.Project.Clips[0].Id;
            _doc.AddKeyframe(id, TransformProperty.X, 0, 0);
            _doc.AddKeyframe(id, TransformProperty.X, 5, 12);
            _doc.AddKeyframe(id, TransformProperty.X, 5, 99); // replace
            _doc.AddKeyframe(id, TransformProperty.ScaleY, 3, 2.5, KeyframeInterpolation.Linear);
            _seqKeyProp = TransformProperty.X; _seqKeyFrame = 5;
            StepKeyframe(1); StepKeyframe(-1);
            SelectKeyframe(TransformProperty.ScaleY, 3, false);
            _doc.MoveKeyframe(id, TransformProperty.X, 5, 7);
            _doc.MoveKeyframe(id, TransformProperty.X, 7, 7); // no-op same frame
            _doc.SetKeyframeValue(id, TransformProperty.X, 7, -4, KeyframeInterpolation.Hold);
            RebuildSequence();
            AuditCheck(_doc.RemoveKeyframe(id, TransformProperty.X, 7), "delete key");
            AuditCheck(!_doc.RemoveKeyframe(id, TransformProperty.X, 7), "delete missing key false");
            RebuildSequence();
            AuditInvariants("keyframes");
        });

        AuditStep("keyframe.invalid-rejected", () =>
        {
            var id = _doc.Project.Clips[0].Id;
            bool threw = false;
            try { _doc.AddKeyframe(id, TransformProperty.X, 1, double.PositiveInfinity); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            AuditCheck(threw, "Inf keyframe rejected");
            threw = false;
            try { _doc.AddKeyframe(id, TransformProperty.X, -1, 1); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            AuditCheck(threw, "negative frame rejected");
            threw = false;
            try { _doc.MoveKeyframe(id, TransformProperty.X, 99, 5); }
            catch (KeyNotFoundException) { threw = true; }
            AuditCheck(!threw, "move missing key returns false (no throw)");
            threw = false;
            try { _doc.SetKeyframeValue(id, TransformProperty.X, 999, 1, KeyframeInterpolation.Hold); }
            catch (KeyNotFoundException) { threw = true; }
            AuditCheck(threw, "edit missing key throws KeyNotFound");
        });
        AuditStep("keyframe.invalid-fps+speed-rejected", () =>
        {
            var id = _doc.Project.Clips[0].Id;
            int bad = 0;
            try { _doc.SetFps(0); } catch (ArgumentOutOfRangeException) { bad++; }
            try { _doc.SetFps(double.NaN); } catch (ArgumentOutOfRangeException) { bad++; }
            try { _doc.SetClipSpeed(id, -1); } catch (ArgumentOutOfRangeException) { bad++; }
            try { _doc.SetClipSpeed("missing-id", 1); } catch (KeyNotFoundException) { bad++; }
            AuditCheck(bad == 4, "all invalid timing rejected");
        });
        AuditStep("fpsbox.unknown-value-noop", () =>
        {
            // A project fps with no matching combo entry must not crash or change state.
            _doc.SetFps(29.97);
            RebuildSequence();
            AuditCheck(Math.Abs(_doc.Project.Fps - 29.97) < 1e-9, "fps kept");
            _doc.SetFps(30);
            RebuildSequence();
        });
        AuditStep("select.empty-engine-safe", () =>
        {
            // Selecting targets while the engine has no loaded clip must not throw.
            _doc.New("Empty Select");
            _doc.AddEffect(fxPath, 0, 5);
            _doc.AddAudio(wavPath, 0.0);
            SetSequenceMode(true);
            RebuildSequence(); // clips empty -> engine unloaded
            SelectSeqTarget(SeqTargetKind.Effect, _doc.Project.Effects[0].Id);
            SelectSeqTarget(SeqTargetKind.Audio, _doc.Project.Audio[0].Id);
            SelectSeqTarget(SeqTargetKind.Clip, "missing-clip"); // unknown id: no-op-ish path
            RefreshFrame();
            AuditInvariants("select-empty-engine");
            _doc.New("Audit");
            RebuildSequence();
        });
        // ---------- 7. Effects (fxPath/wavPath declared up top) ----------
        AuditStep("effect.missing-file-skipped", () =>
        {
            var e = _doc.AddEffect(Path.Combine(dir, "nope.png"), 0, 5);
            RebuildSequence(); // must not throw
            _doc.RemoveEffect(e.Id);
            RebuildSequence();
        });
        AuditStep("effect.corrupt-file", () =>
        {
            string bad = Path.Combine(dir, "corrupt.png");
            File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5 });
            var e = _doc.AddEffect(bad, 0, 5);
            RebuildSequence(); // decode fails -> skipped, no crash
            AuditCheck(true, "survived corrupt png");
            _doc.RemoveEffect(e.Id);
            RebuildSequence();
        });
        AuditStep("effect.add+move+layer+transform+keys", () =>
        {
            var e = _doc.AddEffect(fxPath, 4, 10);
            _doc.MoveEffect(e.Id, 8);
            _doc.ResizeEffect(e.Id, 12);
            _doc.SetEffectLayer(e.Id, 3);
            _doc.SetEffectTransform(e.Id, Transform.Identity with { ScaleX = 2, RotationDegrees = 45 });
            _doc.AddEffectKeyframe(e.Id, TransformProperty.Opacity, 0, 1);
            _doc.AddEffectKeyframe(e.Id, TransformProperty.Opacity, 11, 0, KeyframeInterpolation.Linear);
            _doc.MoveEffectKeyframe(e.Id, TransformProperty.Opacity, 11, 10);
            SelectSeqTarget(SeqTargetKind.Effect, e.Id);
            RebuildSequence();
            AuditCheck(_doc.RemoveEffectKeyframe(e.Id, TransformProperty.Opacity, 10), "delete fx key");
            RebuildSequence();
            AuditInvariants("effects");
        });

        // ---------- 8. Audio (wavPath declared up top) ----------
        AuditStep("audio.missing-file", () =>
        {
            var a = _doc.AddAudio(Path.Combine(dir, "nope.wav"), 0.25);
            RebuildSequence();
            TogglePlay(); AuditTick(3); TogglePlay(); // voices must degrade gracefully
            _doc.RemoveAudio(a.Id);
            RebuildSequence();
        });
        AuditStep("audio.full-cycle", () =>
        {
            _doc.AddClip(A(), 1.0);
            RebuildSequence();
            var a = _doc.AddAudio(wavPath, 0.0);
            _doc.MoveAudio(a.Id, 0.5);
            _doc.TrimAudio(a.Id, 0.1, 0.5);
            _doc.SetAudioVolume(a.Id, 0.3);
            _doc.SetAudioMuted(a.Id, true);
            _doc.SetAudioMuted(a.Id, false);
            SelectSeqTarget(SeqTargetKind.Audio, a.Id);
            RebuildSequence();
            TogglePlay(); AuditTick(4); TogglePlay();
            AuditInvariants("audio");
        });

        // ---------- 9. Undo/redo matrix (UI-level: selection fixup + rebuild) ----------
        AuditStep("undo.drain-all-plus-extra", () =>
        {
            int guard = 0;
            while (_doc.History.CanUndo && guard++ < 500) { Undo(); }
            AuditCheck(!_doc.History.CanUndo, "history drained");
            Undo(); Undo(); // extra undos are safe no-ops
            AuditCheck(!_doc.History.CanUndo, "still empty");
            RefreshFrame();
            AuditInvariants("undo-drained");
        });
        AuditStep("redo.drain-all-plus-extra", () =>
        {
            int guard = 0;
            while (_doc.History.CanRedo && guard++ < 500) { Redo(); }
            AuditCheck(!_doc.History.CanRedo, "redo drained");
            Redo(); Redo();
            RefreshFrame();
            AuditInvariants("redo-drained");
        });
        AuditStep("undo.redo-cleared-by-new-edit", () =>
        {
            _doc.Undo(); // creates redo
            AuditCheck(_doc.History.CanRedo, "redo available");
            _doc.AddClip(A(), 1.0); // new edit clears redo
            AuditCheck(!_doc.History.CanRedo, "redo cleared by edit");
            RebuildSequence();
        });
        AuditStep("undo.dirty-tracking", () =>
        {
            _doc.MarkSaved(Path.Combine(dir, "dirty.dcanim"));
            AuditCheck(!_doc.IsDirty, "clean after save-mark");
            _doc.AddClip(A(), 1.0);
            AuditCheck(_doc.IsDirty, "dirty after edit");
            _doc.Undo();
            AuditCheck(!_doc.IsDirty, "clean after undo-to-saved");
            _doc.Redo();
            AuditCheck(_doc.IsDirty, "dirty after redo");
            RebuildSequence();
        });
        AuditStep("undo.history-cap", () =>
        {
            for (int i = 0; i < 115; i++)
            {
                _doc.SetClipSpeed(_doc.Project.Clips[0].Id, 1.0 + (i % 5) * 0.1);
            }
            int n = 0;
            while (_doc.History.CanUndo && n++ < 1000) _doc.Undo();
            AuditCheck(n <= 101, $"cap respected (undid {n})");
            _doc.Redo();
            RebuildSequence();
        });
        AuditStep("undo.delete-final-clip+keyframe", () =>
        {
            var id = _doc.Project.Clips[0].Id;
            _doc.AddKeyframe(id, TransformProperty.Y, 2, 9);
            _doc.RemoveClip(id);
            RebuildSequence();
            _doc.Undo(); // clip back
            RebuildSequence();
            AuditCheck(_doc.Project.Clips.Any(c => c.Id == id), "clip restored");
            _doc.Undo(); // keyframe add undone... (order!) just don't crash
            RebuildSequence();
            AuditInvariants("undo-delete");
        });

        // ---------- 10. Projects ----------
        AuditStep("project.save-reopen-roundtrip", async () =>
        {
            string p = Path.Combine(dir, "roundtrip.dcanim");
            ProjectSerializer.Save(_doc.Project, p);
            _doc.MarkSaved(p);
            var loaded = ProjectSerializer.Load(p);
            _doc.Load(loaded, p);
            RebuildSequence();
            AuditCheck(_doc.Project.Clips.Count > 0, "roundtrip keeps clips");
            await Task.CompletedTask;
        });
        AuditStep("project.malformed+json-errors", () =>
        {
            foreach (var (name, content) in new[] {
                ("bad.json", "{ nope"),
                ("empty.json", ""),
                ("wrongtag.json", """{"format":"nope","version":1,"name":"x","fps":30,"clips":[]}"""),
                ("badver.json", """{"format":"dcanim","version":99,"name":"x","fps":30,"clips":[]}"""),
                ("badfps.json", """{"format":"dcanim","version":2,"name":"x","fps":0,"clips":[]}"""),
            })
            {
                string p = Path.Combine(dir, name);
                File.WriteAllText(p, content);
                bool threw = false;
                try { ProjectSerializer.Load(p); }
                catch (InvalidDataException) { threw = true; }
                catch (NotSupportedException) { threw = true; }
                AuditCheck(threw, name + " rejected");
            }
            // Current project must be untouched by failed loads.
            AuditCheck(_doc.Project.Clips.Count > 0, "project intact after bad loads");
            RebuildSequence();
        });
        AuditStep("project.missing-file", () =>
        {
            bool threw = false;
            try { ProjectSerializer.Load(Path.Combine(dir, "absent.dcanim")); }
            catch (InvalidDataException) { threw = true; }
            AuditCheck(threw, "missing file rejected");
        });
        AuditStep("project.v1-loads", () =>
        {
            string p = Path.Combine(dir, "v1test.dcanim");
            File.WriteAllText(p,
                """{"format":"dcanim","version":1,"name":"Legacy","fps":30,"clips":[{"id":"c1","animation":"AtkKatanaB2","speed":1}]}""");
            var loaded = ProjectSerializer.Load(p);
            AuditCheck(loaded.Version == SequenceProject.CurrentVersion && loaded.Clips.Count == 1, "v1 migrated");
            AuditCheck(loaded.Clips[0].Keyframes.Count == 0, "v1 keys default");
            var lc = loaded.Clips[0];
            AuditCheck(lc.SourceFirst == 0 && lc.SourceLast is null && !lc.Reversed &&
                lc.LeadHoldFrames == 0 && lc.TailHoldFrames == 0, "v1 splice defaults");
        });
        AuditStep("project.missing-anim-ids", () =>
        {
            string p = Path.Combine(dir, "missing.dcanim");
            File.WriteAllText(p,
                """{"format":"dcanim","version":2,"name":"M","fps":30,"clips":[{"id":"c1","animation":"Nope_Missing","speed":1,"transform":null,"keyframes":[]}],"effects":[],"audio":[]}""");
            var loaded = ProjectSerializer.Load(p);
            _doc.Load(loaded, p);
            RebuildSequence(); // skipped with toast, no crash
            AuditCheck(true, "missing anim tolerated");
            _doc.New("Audit");
            RebuildSequence();
        });

        // ---------- 11. Timeline ops ----------
        AuditStep("timeline.zoom+scrub+seek", () =>
        {
            _doc.AddClip(A(), 1.0);
            _doc.AddClip(B(), 1.0);
            RebuildSequence();
            Timeline.SetZoomFactor(0.5);
            Timeline.SetZoomFactor(8);
            Timeline.SetZoomFactor(2);
            _engine.Scrub(0); _engine.Scrub(0.33); _engine.Scrub(1);
            RefreshFrame();
            _engine.SeekFrame(0);
            _engine.SeekFrame(Math.Max(0, ActiveFrameCount - 1));
            TimelinePanel.Height = 140;
            TimelinePanel.Height = 400;
            TimelinePanel.Height = 230;
            AuditInvariants("timeline-ops");
        });
        AuditStep("timeline.empty-scrub", () =>
        {
            _doc.New("Empty2");
            RebuildSequence();
            RefreshFrame();
            TogglePlay();
            AuditInvariants("empty-refresh");
        });

        // ---------- 12. Mode switches under load ----------
        AuditStep("modes.switch-while-playing", () =>
        {
            _doc.AddClip(A(), 1.0);
            RebuildSequence();
            SetSequenceMode(true);
            TogglePlay();
            SetSequenceMode(false);
            SetSequenceMode(true);
            Browser.Select(A());
            TogglePlay(); TogglePlay();
            AuditInvariants("mode-switch");
        });

        // ---------- 13. Viewport drag incl. cancel paths ----------
        AuditStep("drag.viewport-ui-path", () =>
        {
            _doc.New("DragUI");
            var c = _doc.AddClip(A(), 1.0);
            RebuildSequence();
            SelectSeqTarget(SeqTargetKind.Clip, c.Id);
            OnViewportDragStart();
            // Real drags always span more than the 40ms coalescing window;
            // yield once so the throttled move path engages like a human drag.
            System.Threading.Thread.Sleep(60);
            OnViewportMove(new Avalonia.Vector(48, -24));
            OnViewportDragEnd();
            RebuildSequence();
            var t = _doc.Project.Clips.First(x => x.Id == c.Id).BaseTransform;
            AuditCheck(Math.Abs(t.X - 48 / Math.Max(1.0, Viewport.CurrentZoom)) < 1.0, "viewport drag moved clip");
            AuditInvariants("viewport-drag-ui");
        });
        AuditStep("keyframe.ui-path", () =>
        {
            _doc.New("Keys");
            var c = _doc.AddClip(A(), 1.0);
            RebuildSequence();
            SelectSeqTarget(SeqTargetKind.Clip, c.Id);
            // Add via the UI handler path (uses playhead position).
            _engine.SeekFrame(0);
            AddKeyframeAtPlayhead(TransformProperty.X);
            _engine.SeekFrame(0);
            MoveSelectedKeyframeTo(4); // real UI path; target+selection already set
            var moved = _doc.Project.Clips.First(x => x.Id == c.Id).Keyframes;
            AuditCheck(moved.Any(k => k.Property == TransformProperty.X && k.Frame == 4),
                "keyframe moved via UI path");
            _doc.SetKeyframeValue(c.Id, TransformProperty.X, 4, 7, KeyframeInterpolation.Linear);
            SelectKeyframe(TransformProperty.X, 4, false);
            var before = _doc.Project.Clips.First(x => x.Id == c.Id).Keyframes.Count;
            DeleteKeyframe(TransformProperty.X, 4);
            var after = _doc.Project.Clips.First(x => x.Id == c.Id).Keyframes.Count(k => k.Property == TransformProperty.X);
            AuditCheck(before == after + 1, "keyframe deleted via UI path");
            StepKeyframe(1); StepKeyframe(-1);
            RebuildSequence();
            AuditInvariants("keyframe-ui");
        });
        AuditStep("overlay.cross-clip-selection", () =>
        {
            // Regression: with 2+ clips of different source lengths, keeping
            // the SHORT clip selected while the playhead sat in the LONG
            // clip's segment made the selection overlay request a source
            // index valid for the long animation but out of range for the
            // selected one — a per-frame crash during playback.
            _doc.New("Overlay");
            var clipA = _doc.AddClip("AtkKatanaA", 1.0);
            var clipB = _doc.AddClip(B(), 1.0);
            RebuildSequence();
            var rA = _seqResolved.First(r => r.Clip.Id == clipA.Id);
            var rB = _seqResolved.First(r => r.Clip.Id == clipB.Id);
            AuditCheck(rA.SourceFrames != rB.SourceFrames, "clips have different source lengths");
            // Select whichever clip is SHORTER and park the playhead in the
            // LONGER clip's segment: SourceIndex from the long segment can
            // exceed the short animation's frame count.
            var (selR, hostR) = rA.SourceFrames <= rB.SourceFrames ? (rA, rB) : (rB, rA);
            SelectSeqTarget(SeqTargetKind.Clip, selR.Clip.Id);
            // Park the playhead on the LAST frame of the LONGER clip's
            // segment: SourceIndex there returns SourceFrames-1 (the maximum),
            // which is out of range for the shorter selected animation.
            _engine.SeekFrame(hostR.StartFrame + hostR.OutputFrames - 1);
            RefreshFrame();
            // And sweep the tail during playback, as a real play would.
            _engine.SeekFrame(hostR.StartFrame + Math.Max(0, hostR.OutputFrames - 6));
            AuditTick(6); // would throw every frame before the fix
            SelectSeqTarget(SeqTargetKind.Clip, hostR.Clip.Id);
            _engine.SeekFrame(0);
            RefreshFrame();
            AuditTick(10);
            AuditInvariants("overlay-cross");
        });
        AuditStep("viewport.drag-doclevel", () =>
        {
            _doc.New("Drag");
            var c = _doc.AddClip(A(), 1.0);
            RebuildSequence();
            SelectSeqTarget(SeqTargetKind.Clip, c.Id);
            _doc.BeginDrag("Move selection");
            _doc.SetClipTransformDirect(c.Id, Transform.Identity with { X = 30, Y = -20 });
            RebuildSequence();
            RefreshFrame();
            _doc.EndDrag();
            RebuildSequence();
            // abort path with nothing pending is a safe no-op
            _doc.AbortDrag();
            RebuildSequence();
            AuditInvariants("viewport-drag");
        });

        // ---------- 13b. Drag-cancel and mid-gesture safety ----------
        AuditStep("drag.keyframe-abort-restores", () =>
        {
            var c = _doc.AddClip(A(), 1.0);
            RebuildSequence();
            var before = _doc.Project.Clips.First(x => x.Id == c.Id).Keyframes.Count;
            _doc.BeginDrag("Move keyframe");
            _doc.MoveKeyframeDirect(c.Id, TransformProperty.X, 0, 3);
            _doc.AbortDrag();
            RebuildSequence();
            var after = _doc.Project.Clips.First(x => x.Id == c.Id).Keyframes.Count(k => k.Property == TransformProperty.X && k.Frame == 3);
            AuditCheck(before == 0 && after == 0, "aborted drag left no keyframe");
            AuditInvariants("drag-abort");
        });
        AuditStep("drag.delete-target-mid-gesture", () =>
        {
            _doc.New("DragKill");
            var c = _doc.AddClip(A(), 1.0);
            RebuildSequence();
            SelectSeqTarget(SeqTargetKind.Clip, c.Id);
            _doc.BeginDrag("Move selection");
            OnViewportDragStart();
            _doc.RemoveClip(c.Id); // target vanishes mid-gesture
            OnViewportMove(new Avalonia.Vector(50, 50)); // must abort, not throw
            OnViewportDragEnd();
            RebuildSequence();
            AuditInvariants("drag-delete");
            SelectSeqTarget(SeqTargetKind.Clip, _doc.AddClip(A(), 1.0).Id);
            RebuildSequence();
        });
        AuditStep("undo.coalesced-drag-single-step", () =>
        {
            var c = _doc.Project.Clips[^1];
            // Verify single-step-ness: one undo must revert the whole gesture.
            _doc.BeginDrag("Move selection");
            var startX = c.BaseTransform.X;
            _doc.SetClipTransformDirect(c.Id, Transform.Identity with { X = 10 });
            _doc.SetClipTransformDirect(c.Id, Transform.Identity with { X = 20 });
            _doc.SetClipTransformDirect(c.Id, Transform.Identity with { X = 30 });
            _doc.EndDrag();
            RebuildSequence();
            var afterX = _doc.Project.Clips.First(x => x.Id == c.Id).BaseTransform.X;
            AuditCheck(Math.Abs(afterX - 30) < 1e-9, "drag applied");
            _doc.Undo(); // must revert the WHOLE gesture in one step
            RebuildSequence();
            var undoneX = _doc.Project.Clips.First(x => x.Id == c.Id).BaseTransform.X;
            AuditCheck(Math.Abs(undoneX - startX) < 1e-9, "single undo reverted whole drag");
            _doc.Redo();
            RebuildSequence();
            var redoneX = _doc.Project.Clips.First(x => x.Id == c.Id).BaseTransform.X;
            AuditCheck(Math.Abs(redoneX - 30) < 1e-9, "redo restored whole drag");
            AuditInvariants("coalesced-drag");
        });
        AuditStep("drag.viewport-ui-path", () =>
        {
            // Exercise the actual UI drag handlers (selection + throttle + commit).
            _doc.New("DragUI");
            var c = _doc.AddClip(A(), 1.0);
            RebuildSequence();
            SelectSeqTarget(SeqTargetKind.Clip, c.Id);
            OnViewportDragStart();
            OnViewportMove(new Avalonia.Vector(60, -40));
            RefreshFrame();
            OnViewportDragEnd();
            RebuildSequence();
            AuditInvariants("viewport-drag-ui");
        });
        AuditStep("keyframe.move-ui-path", () =>
        {
            var c = _doc.Project.Clips[0];
            _doc.AddKeyframe(c.Id, TransformProperty.Y, 2, 9);
            RebuildSequence();
            SelectSeqTarget(SeqTargetKind.Clip, c.Id);
            _seqKeyProp = TransformProperty.Y;
            _seqKeyFrame = 2;
            MoveSelectedKeyframeTo(6);
            var moved = _doc.Project.Clips.First(x => x.Id == c.Id).Keyframes;
            AuditCheck(moved.Any(k => k.Property == TransformProperty.Y && k.Frame == 6),
                "keyframe moved via UI path");
            AuditInvariants("keyframe-move-ui");
        });

        // ---------- AS-M4.2: splicing ----------
        AuditStep("splice.trim-inspector-path", () =>
        {
            _doc.New("Splice");
            var a = _doc.AddClip(A(), 1.0);
            RebuildSequence();
            SelectSeqTarget(SeqTargetKind.Clip, a.Id);
            // Invalid trims toast instead of throwing.
            SetTrimForSelection(0, 99);
            SetTrimForSelection(99, null);
            var r0 = _seqResolved.First(x => x.Clip.Id == a.Id);
            AuditCheck(r0.SourceFrames == _loader!.Catalog.GetFrameRegions(A()).Count, "invalid trims clamped");
            // Valid trim via the inspector path (1-based).
            SetTrimForSelection(2, 6);
            var r = _seqResolved.First(x => x.Clip.Id == a.Id);
            AuditCheck(r.SourceFirst == 1 && r.SourceFrames == 5, "trim applied");
            AuditCheck(TotalSeqFrames() == 5, "duration follows trim");
            Undo(); // trim undone
            AuditCheck(TotalSeqFrames() == _loader.Catalog.GetFrameRegions(A()).Count, "undo restores range");
            Redo();
            AuditCheck(TotalSeqFrames() == 5, "redo restores trim");
            AuditInvariants("trim");
        });
        AuditStep("splice.trim-drag-path", () =>
        {
            var a = _doc.Project.Clips[0];
            SelectSeqTarget(SeqTargetKind.Clip, a.Id);
            var r = _seqResolved.First(x => x.Clip.Id == a.Id);
            _doc.BeginDrag("Trim clip");
            OnTrimming(a.Id, true, r.StartFrame + 1); // left edge +1 output
            _doc.EndDrag();
            AfterStructuralEdit();
            var r2 = _seqResolved.First(x => x.Clip.Id == a.Id);
            AuditCheck(r2.SourceFirst == r.SourceFirst + 1, "drag trimmed left edge");
            AuditCheck(TotalSeqFrames() == 4, "duration shrank by one");
            _doc.AbortDrag(); // nothing open: safe no-op
            AuditInvariants("trim-drag");
            Undo();
            AuditCheck(TotalSeqFrames() == 5, "drag-trim undone in one step");
        });
        AuditStep("splice.split-reverse-hold", () =>
        {
            _doc.New("Splice2");
            var a = _doc.AddClip(A(), 1.0);
            var b = _doc.AddClip(B(), 1.0);
            RebuildSequence();
            int totalBefore = TotalSeqFrames();
            SelectSeqTarget(SeqTargetKind.Clip, a.Id);
            _engine.SeekFrame(2);
            SplitAtPlayhead();
            AuditCheck(_doc.Project.Clips.Count == 3, "split produced two clips from one");
            AuditCheck(TotalSeqFrames() == totalBefore, "1x split preserves total");
            _seqSelectedClip = b.Id;
            SelectSeqTarget(SeqTargetKind.Clip, b.Id);
            ReverseSelected();
            var rb = _seqResolved.First(x => x.Clip.Id == b.Id);
            AuditCheck(rb.Reversed, "clip reversed");
            SetHoldForSelection(6);
            var rb2 = _seqResolved.First(x => x.Clip.Id == b.Id);
            AuditCheck(rb2.OutputFrames == rb.OutputFrames + 6, "hold extends duration");
            AuditCheck(rb2.Map.SourceAt(rb2.OutputFrames - 1) == rb2.Map.HoldSource, "hold repeats final frame");
            TogglePlay(); AuditTick(10); TogglePlay(); // playback across trim/split/reverse/hold
            AuditCheck(!_engine.IsPlaying, "paused after tick");
            AuditInvariants("split-playback");
            _engine.SeekFrame(2); // back to the seam
            SplitAtPlayhead(); // at seam: harmless no-op
            AuditCheck(_doc.Project.Clips.Count == 3, "seam split changes nothing");
            AuditShot("audit-04-split-hold");
            // No phantom undo entry for the no-op: undos revert hold, reverse, split in order.
            Undo();
            AuditCheck(_doc.Project.Clips.First(x => x.Id == b.Id).TailHoldFrames == 0, "hold undone first");
            Undo();
            AuditCheck(!_doc.Project.Clips.First(x => x.Id == b.Id).Reversed, "reverse undone");
            Undo();
            AuditCheck(_doc.Project.Clips.Count == 2, "split undone");
            AuditInvariants("split-reverse-hold");
        });
        await AuditStepAsync("splice.save-reopen", async () =>
        {
            _doc.New("SpliceSave");
            var a = _doc.AddClip(A(), 1.0);
            _doc.SetTrim(a.Id, 2, 6, _loader!.Catalog.GetFrameRegions(A()).Count);
            var b = _doc.AddClip(B(), 1.0);
            _doc.SetReversed(b.Id, true, _loader.Catalog.GetFrameRegions(B()).Count);
            _doc.SetTailHold(b.Id, 4, _loader.Catalog.GetFrameRegions(B()).Count);
            RebuildSequence();
            int clipsBefore = _doc.Project.Clips.Count;
            int framesBefore = TotalSeqFrames();
            string sp = Path.Combine(dir, "splice-roundtrip.dcanim");
            ProjectSerializer.Save(_doc.Project, sp);
            _doc.MarkSaved(sp);
            _doc.New("Empty");
            RebuildSequence();
            AuditCheck(TotalSeqFrames() == 0, "emptied");
            await OpenProjectFileAsync(sp);
            AuditCheck(_doc.Project.Clips.Count == clipsBefore, "clips survive reopen");
            AuditCheck(TotalSeqFrames() == framesBefore, "frames survive reopen");
            var ra = _seqResolved.First(x => x.Clip.AnimationId == A());
            AuditCheck(ra.SourceFirst == 2 && ra.SourceFrames == 5, "trim survives reopen");
            var rbb = _seqResolved.First(x => x.Clip.AnimationId == B());
            AuditCheck(rbb.Reversed && rbb.TailHoldFrames == 4, "reverse+hold survive reopen");
            AuditInvariants("splice-save-reopen");
        });
        AuditStep("splice.copy-paste-delete", () =>
        {
            _doc.New("Splice3");
            _doc.AddClip(A(), 1.0);
            _doc.AddClip(B(), 1.0);
            RebuildSequence();
            int n0 = TotalSeqFrames();
            SetFrameSelection(2, 7);
            CopySelection();
            PasteAtPlayhead();
            AuditCheck(TotalSeqFrames() == n0 + 5, "paste inserted 5 frames");
            Undo();
            AuditCheck(TotalSeqFrames() == n0, "paste undone");
            Redo();
            AuditCheck(TotalSeqFrames() == n0 + 5, "paste redone");
            SetFrameSelection(2, 7);
            DeleteSelection();
            AuditCheck(TotalSeqFrames() == n0, "delete removed the pasted range");
            ClearFrameSelection();
            DuplicateSelection(); // no selection, no clip target: safe toast
            AuditCheck(TotalSeqFrames() == n0, "duplicate with no target is a no-op");
            AuditInvariants("copy-paste-delete");
        });
        AuditStep("splice.ripple-policy", () =>
        {
            _doc.New("Ripple");
            _doc.AddClip(A(), 1.0);
            var fxPath = Path.Combine(dir, "shot.png");
            var fx = _doc.AddEffect(fxPath, 4, 6);
            var au = _doc.AddAudio(wavPath, 1.0);
            RebuildSequence();
            AuditCheck(_doc.Project.RippleFxAudio, "ripple defaults on");
            SetFrameSelection(0, 2);
            DeleteSelection(); // ripple: fx/audio shift -2
            var fxAfter = _doc.Project.Effects.First(e => e.Id == fx.Id);
            AuditCheck(fxAfter.StartFrame == 2, "effect rippled with delete");
            var auAfter = _doc.Project.Audio.First(a => a.Id == au.Id);
            AuditCheck(Math.Abs(auAfter.StartSeconds - (1.0 - 2.0 / 30.0)) < 1e-9, "audio rippled with delete");
            Undo();
            _doc.SetRipple(false);
            SetFrameSelection(0, 2);
            DeleteSelection();
            fxAfter = _doc.Project.Effects.First(e => e.Id == fx.Id);
            AuditCheck(fxAfter.StartFrame == 4, "ripple off: effect stays absolute");
            Undo();
            _doc.SetRipple(true);
            UpdateRippleMenu();
            ClearFrameSelection();
            AuditInvariants("ripple");
        });
        AuditStep("splice.onion-preview", () =>
        {
            _doc.New("Onion");
            _doc.AddClip(A(), 1.0);
            _doc.AddClip(B(), 1.0);
            RebuildSequence();
            _engine.SeekFrame(3);
            SetOnionEnabled(true);
            SetOnionCount(2);
            SetOnionOpacity(0.35);
            RefreshFrame();
            AuditCheck(Viewport.Preview.OnionPrevious is { Count: 2 }, "two past ghosts");
            AuditCheck(Viewport.Preview.OnionNext is { Count: 2 }, "two future ghosts");
            _engine.SeekFrame(0);
            RefreshFrame();
            AuditCheck(Viewport.Preview.OnionPrevious is { Count: 0 }, "clamped at start");
            AuditShot("audit-05-onion");
            // Onion skins never reach exports: frame count follows the clip.
            var rs = new RenderedSequence(_seqRendered!.Width, _seqRendered.Height,
                _seqClip!.Frames.Select(f => f.Rgba).ToList());
            AuditCheck(rs.Frames.Count == _seqClip.FrameCount, "export source unaffected by onion");
            SetOnionEnabled(false);
            AuditInvariants("onion");
        });
        AuditStep("splice.selection-state", () =>
        {
            _doc.New("Sel");
            _doc.AddClip(A(), 1.0);
            _doc.AddClip(B(), 1.0);
            RebuildSequence();
            SetFrameSelection(1, 4);
            AuditCheck(HasFrameSelection, "selection set");
            // Structural edits invalidate/keep the selection sanely.
            _doc.New("Sel2");
            RebuildSequence();
            AuditCheck(!HasFrameSelection, "new project clears selection");
            SetFrameSelection(0, 100000);
            DeleteSelection(); // clamped to the sequence
            AuditCheck(TotalSeqFrames() == 0, "oversized range deletes all");
            AuditCheck(!HasFrameSelection, "selection cleared after delete");
            AuditInvariants("selection-state");
            // Exports below run on a spliced project (trim + reverse + hold).
            var repop = _doc.AddClip(A(), 1.0);
            var repopB = _doc.AddClip(B(), 1.0);
            RebuildSequence();
            SelectSeqTarget(SeqTargetKind.Clip, repopB.Id);
            SetTrimForSelection(2, 6);
            SetHoldForSelection(3);
            SelectSeqTarget(SeqTargetKind.Clip, repop.Id);
        });
        AuditStep("export.png-sequence", () =>
        {
            var rs = new RenderedSequence(_seqRendered!.Width, _seqRendered.Height,
                _seqClip!.Frames.Select(f => f.Rgba).ToList());
            string outDir = Path.Combine(dir, "exp-png");
            var files = PngSequenceExporter.ExportRendered(rs, outDir, overwrite: true).ToList();
            AuditCheck(files.Count == _seqClip.FrameCount && File.Exists(files[0]), "png files written");
        });
        AuditStep("export.gif", () =>
        {
            var rs = new RenderedSequence(_seqRendered!.Width, _seqRendered.Height,
                _seqClip!.Frames.Select(f => f.Rgba).ToList());
            string gp = Path.Combine(dir, "exp.gif");
            var info = GifExporter.ExportRendered(rs, _doc.Project.Fps, gp, loop: false, overwrite: true);
            AuditCheck(info.FrameCount == _seqClip!.FrameCount, "gif frame count");
        });
        AuditStep("export.sheet", () =>
        {
            var rs = new RenderedSequence(_seqRendered!.Width, _seqRendered.Height,
                _seqClip!.Frames.Select(f => f.Rgba).ToList());
            var info = SpriteSheetExporter.Export(rs, _seqResolved, _doc.Project.Fps,
                Path.Combine(dir, "exp-sheet.png"), Path.Combine(dir, "exp-sheet.json"),
                4, 2, overwrite: true);
            AuditCheck(info.Frames.Count == _seqClip!.FrameCount, "sheet frames");
            AuditCheck(info.Clips.Count == _seqResolved.Count, "sheet clip boundaries");
        });
        AuditStep("export.empty-project", () =>
        {
            _doc.New("Empty Export");
            RebuildSequence();
            bool threw = false;
            try
            {
                PngSequenceExporter.ExportRendered(new RenderedSequence(0, 0, Array.Empty<byte[]>()),
                    Path.Combine(dir, "exp-empty"), overwrite: true);
            }
            catch (InvalidDataException) { threw = true; }
            AuditCheck(threw, "empty export refused cleanly");
        });
        AuditStep("export.invalid-dest", () =>
        {
            _doc.AddClip(A(), 1.0);
            RebuildSequence();
            var rs = new RenderedSequence(_seqRendered!.Width, _seqRendered.Height,
                _seqClip!.Frames.Select(f => f.Rgba).ToList());
            bool threw = false;
            try { PngSequenceExporter.ExportRendered(rs, Path.Combine(dir, "bad*name", "out.png"), overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { threw = true; }
            AuditCheck(threw, "illegal-char path rejected");
        });
        await AuditStepAsync("export.final-showcase", async () =>
        {
            // Finale: a genuinely spliced Katana sequence with trim, reverse,
            // hold, effect, audio and onion skins — the evidence screenshot.
            _doc.New("Spliced Finale");
            var fa = _doc.AddClip("AtkKatanaA", 1.0);
            _doc.SetTrim(fa.Id, 0, 7, _loader!.Catalog.GetFrameRegions("AtkKatanaA").Count);
            var fb = _doc.AddClip(B(), 1.0);
            _doc.SetReversed(fb.Id, true, _loader.Catalog.GetFrameRegions(B()).Count);
            _doc.SetTailHold(fb.Id, 4, _loader.Catalog.GetFrameRegions(B()).Count);
            var fx = _doc.AddEffect(Path.Combine(dir, "shot.png"), 4, 8);
            _doc.AddAudio(wavPath, 0.25);
            RebuildSequence();
            SelectSeqTarget(SeqTargetKind.Clip, fb.Id);
            _engine.SeekFrame(TotalSeqFrames() / 2);
            SetOnionEnabled(true);
            SetOnionCount(2);
            RefreshFrame();
            AuditCheck(TotalSeqFrames() > 0, "finale populated");
            // Let transient toasts expire so the evidence shot is clean.
            await Task.Delay(6500);
            AuditShot("audit-final-state");
            SetOnionEnabled(false);
        });
        await FinishAuditAsync();
    }

    private async Task FinishAuditAsync()
    {
        try
        {
            File.AppendAllText(Path.Combine(_auditDir, "AUDIT_LOG.txt"),
                $"AS-M4 audit finished: {_auditPass} passed, {_auditFail} failed{Environment.NewLine}");
            Environment.ExitCode = _auditFail > 0 ? 1 : 0;
        }
        catch { }
        try
        {
            // Graceful shutdown first so the app can persist nothing and release audio.
            Close();
            await Task.Delay(2000);
        }
        finally
        {
            // Backstop: headless runs must never linger (e.g. an audio callback
            // thread outliving the window). Normal runs never reach here.
            Environment.Exit(Environment.ExitCode);
        }
    }
}
