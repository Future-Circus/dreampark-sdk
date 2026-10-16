#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.IO;
using DreamPark.EditorTools.AnimationOptimization;
using DreamPark.EditorTools.AudioOptimization;
using DreamPark.EditorTools.TextureOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DreamPark.Editor
{
    /// Oct 2026: SDK optimizer bugs found on CrashCourse and WizardsWay
    /// (core_patches/reports/optimize-cc-ww.md, cc-anim-check.md).
    public sealed class OptimizerFixTests
    {
        private static TextureUsage Texture(string path, int src, int currentMax, float worldMeters,
            string importerType = "Default")
            => new TextureUsage
            {
                assetPath = path, extension = Path.GetExtension(path), fileBytes = (long)src * src * 4,
                sourceWidth = src, sourceHeight = src, sRGBTexture = true, currentMaxSize = currentMax,
                currentImporterType = importerType, kind = TextureUsageKind.WorldRenderer, role = TextureRole.Albedo,
                maxRendererSizeMeters = worldMeters,
                isAtlasLike = importerType == "Sprite" || importerType == "GUI" || TextureSizingPolicy.LooksLikeAtlas(path),
            };

        // ── Textures ───────────────────────────────────────────────────

        [Test]
        public void SingleChannelMasksAreNotSkyboxes()
        {
            var mask = Texture("Assets/Content/G/T_Prop_Smooth.png", 2048, 2048, 1f, "SingleChannel");
            TextureUsageGraph.FinalizeKind(mask);
            Assert.That(mask.kind, Is.Not.EqualTo(TextureUsageKind.Skybox), "a 2D single-channel mask is a world texture");
            var cube = Texture("Assets/Content/G/Sky.png", 2048, 2048, 0f);
            cube.isCubemap = true;
            TextureUsageGraph.FinalizeKind(cube);
            Assert.That(cube.kind, Is.EqualTo(TextureUsageKind.Skybox));
        }

        [Test]
        public void ThePlannerNeverRaisesASize()
        {
            // A 1 m prop would get 1024 from world bounds, but it ships at 512.
            var row = TextureOptimizationPlanner.Plan(new List<TextureUsage>
                { Texture("Assets/Content/G/T_Crate.tga", 2048, 512, 1f) })[0];
            Assert.That(row.targetImporterMaxSize, Is.EqualTo(512));
            Assert.That(TextureSizingPolicy.NeverRaise(1024, 512, 4096), Is.EqualTo(512));
            Assert.That(TextureSizingPolicy.NeverRaise(1024, 2048, 256), Is.EqualTo(256), "never above the source pixels");
        }

        [Test]
        public void AtlasesAndUiKeepTheirSize()
        {
            Assert.That(TextureSizingPolicy.LooksLikeAtlas("Assets/Content/G/UI/T_UI_Icons.png"), Is.True);
            Assert.That(TextureSizingPolicy.LooksLikeAtlas("Assets/Content/G/Hosts/T_Face_Atlas.png"), Is.True);
            Assert.That(TextureSizingPolicy.LooksLikeAtlas("Assets/Content/G/Props/T_Crate_Base.png"), Is.False);
            // A badge sheet on a 10 cm quad: world bounds would say 256.
            var sheet = TextureOptimizationPlanner.Plan(new List<TextureUsage>
                { Texture("Assets/Content/G/T_Badge_Sheet.tga", 1024, 1024, 0.1f) })[0];
            Assert.That(sheet.targetImporterMaxSize, Is.EqualTo(1024));
            var sprite = TextureOptimizationPlanner.Plan(new List<TextureUsage>
                { Texture("Assets/Content/G/Sprite.tga", 1024, 1024, 0.1f, "Sprite") })[0];
            Assert.That(sprite.targetImporterMaxSize, Is.EqualTo(1024));
        }

        [Test]
        public void PlatformOverridesAreClampedAndIosCrunchIsPinned()
        {
            var android = new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = 4096 };
            TextureOptimizationExecutor.PlanPlatformOverride(android, 1024, crunched: true, hasAlpha: false, crunchQuality: 50, pinIosCrunch: false);
            Assert.That(android.maxTextureSize, Is.EqualTo(1024), "an Android 4096 override no longer wins over the plan");
            var small = new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = 256 };
            TextureOptimizationExecutor.PlanPlatformOverride(small, 1024, true, false, 50, false);
            Assert.That(small.maxTextureSize, Is.EqualTo(256), "never raised");

            var opaque = new TextureImporterPlatformSettings { name = "iPhone" };
            TextureOptimizationExecutor.PlanPlatformOverride(opaque, 512, true, false, 50, true);
            Assert.That(opaque.overridden, Is.True);
            Assert.That(opaque.format, Is.EqualTo(TextureImporterFormat.ETC_RGB4Crunched),
                "iOS Automatic is ASTC, which ignores crunch");
            Assert.That(opaque.maxTextureSize, Is.EqualTo(512));
            var alpha = new TextureImporterPlatformSettings { name = "iPhone" };
            TextureOptimizationExecutor.PlanPlatformOverride(alpha, 512, true, true, 50, true);
            Assert.That(alpha.format, Is.EqualTo(TextureImporterFormat.ETC2_RGBA8Crunched));
        }

        // ── Audio ──────────────────────────────────────────────────────

        [Test]
        public void AudioPlanNeverRaisesQualityRateOrFormat()
        {
            var usage = new AudioUsage
            {
                currentCompression = AudioTargetCompression.Vorbis, currentVorbisQuality = 0.45f,
                currentSampleRateOverride = 0,
                currentOverrides = new List<AudioPlatformOverride>
                {
                    new AudioPlatformOverride { platform = "Android", compression = AudioTargetCompression.Vorbis, vorbisQuality = 0.6f, sampleRateOverride = 22050 },
                },
            };
            var row = new AudioPlanRow { usage = usage, targetCompression = AudioTargetCompression.Vorbis, targetVorbisQuality = 0.70f, targetSampleRate = 44100 };
            AudioOptimizationPlanner.NeverRaise(usage, row);
            Assert.That(row.targetVorbisQuality, Is.EqualTo(0.45f).Within(1e-4), "q0.45 is never raised to 0.70");
            Assert.That(row.targetSampleRate, Is.EqualTo(22050), "an override's lower rate is respected");

            var adpcm = new AudioUsage { currentCompression = AudioTargetCompression.ADPCM, currentVorbisQuality = 1f };
            var pcm = new AudioPlanRow { usage = adpcm, targetCompression = AudioTargetCompression.PCM };
            AudioOptimizationPlanner.NeverRaise(adpcm, pcm);
            Assert.That(pcm.targetCompression, Is.EqualTo(AudioTargetCompression.ADPCM), "never a bigger format");
        }

        [Test]
        public void AudioOverridesAreClampedToThePlan()
        {
            var o = new AudioImporterSampleSettings
            {
                compressionFormat = AudioCompressionFormat.ADPCM, quality = 1f,
                sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate,
            };
            var plan = new AudioImporterSampleSettings
            {
                compressionFormat = AudioCompressionFormat.Vorbis, quality = 0.5f,
                sampleRateSetting = AudioSampleRateSetting.OverrideSampleRate, sampleRateOverride = 32000,
            };
            var r = AudioOptimizationExecutor.ClampOverride(o, plan);
            Assert.That(r.compressionFormat, Is.EqualTo(AudioCompressionFormat.Vorbis));
            Assert.That(r.quality, Is.EqualTo(0.5f).Within(1e-4));
            Assert.That(r.sampleRateOverride, Is.EqualTo(32000u));

            var vorbisLow = new AudioImporterSampleSettings { compressionFormat = AudioCompressionFormat.Vorbis, quality = 0.3f };
            Assert.That(AudioOptimizationExecutor.ClampOverride(vorbisLow, plan).quality, Is.EqualTo(0.3f).Within(1e-4),
                "a lower override quality is kept");
        }

        // ── Animation ──────────────────────────────────────────────────

        private static AnimationErrorProbe.PoseSeries Series(params (float deg, float mm)[] frames)
        {
            var s = new AnimationErrorProbe.PoseSeries { paths = new[] { "Hips", "Hips/Spine" } };
            foreach (var f in frames)
            {
                s.rotations.Add(new[] { Quaternion.identity, Quaternion.Euler(0, f.deg, 0) });
                s.positions.Add(new[] { Vector3.zero, new Vector3(f.mm / 1000f, 0, 0) });
            }
            return s;
        }

        [Test]
        public void InBetweenFrameErrorIsMeasuredAndJudged()
        {
            var before = new Dictionary<string, AnimationErrorProbe.PoseSeries> { ["Run"] = Series((0, 0), (10, 0), (20, 0)) };
            var same = new Dictionary<string, AnimationErrorProbe.PoseSeries> { ["Run"] = Series((0, 0), (10, 0), (20, 0)) };
            Assert.That(AnimationErrorProbe.Compare(before, same).WithinLimits, Is.True);

            // The key frames match; the in-between one is cut by 10.4° (CrashCourse R_Foot_Stumble).
            var cornerCut = new Dictionary<string, AnimationErrorProbe.PoseSeries> { ["Run"] = Series((0, 0), (20.4f, 0), (20, 0)) };
            var report = AnimationErrorProbe.Compare(before, cornerCut);
            Assert.That(report.maxRotationDegrees, Is.EqualTo(10.4f).Within(0.05f));
            Assert.That(report.WithinLimits, Is.False);
            Assert.That(report.worstClip, Is.EqualTo("Run"));

            var drift = new Dictionary<string, AnimationErrorProbe.PoseSeries> { ["Run"] = Series((0, 0), (10, 6), (20, 0)) };
            Assert.That(AnimationErrorProbe.Compare(before, drift).WithinLimits, Is.False, "6 mm > 5 mm");
            var tiny = new Dictionary<string, AnimationErrorProbe.PoseSeries> { ["Run"] = Series((0, 0), (10.3f, 2), (20, 0)) };
            Assert.That(AnimationErrorProbe.Compare(before, tiny).WithinLimits, Is.True, "0.3° / 2 mm is within limits");
        }

        // ── Executors never open a modal ───────────────────────────────

        [TestCase("Assets/DreamPark/Editor/TextureOptimizer/TextureOptimizationExecutor.cs")]
        [TestCase("Assets/DreamPark/Editor/AudioOptimizer/AudioOptimizationExecutor.cs")]
        [TestCase("Assets/DreamPark/Editor/AnimationOptimizer/AnimationOptimizationExecutor.cs")]
        public void ExecutorsNeverShowADialog(string path)
        {
            Assert.That(File.ReadAllText(path), Does.Not.Contain("DisplayDialog"),
                "executors run under automation; the windows report result.error");
        }
    }
}
#endif
