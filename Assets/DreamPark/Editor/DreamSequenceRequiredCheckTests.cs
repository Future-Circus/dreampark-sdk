#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using DreamPark;
using DreamPark.PreUploadChecks;
using DreamPark.PreUploadChecks.Checks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class DreamSequenceRequiredCheckTests
{
    private const string ContentId = "__DreamSequenceRequiredCheckTests";
    private const string Root = "Assets/Content/" + ContentId;

    [SetUp]
    public void SetUp()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Content")) AssetDatabase.CreateFolder("Assets", "Content");
        if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets/Content", ContentId);
    }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(Root);
    }

    [Test]
    public void SequenceWithCompatibleSource_IsClean()
    {
        string source = CreateAttraction("A_Fits", 12f, 18f);
        string dream = CreateDream(source);

        CheckResult result = Run(dream);

        Assert.That(result.outcome, Is.EqualTo(CheckOutcome.Clean));
    }

    [Test]
    public void SequenceWhoseSourceNoLongerFits_IsBlocking()
    {
        string source = CreateAttraction("A_TooLarge", 30f, 40f);
        string dream = CreateDream(source);

        CheckResult result = Run(dream);

        Assert.That(result.outcome, Is.EqualTo(CheckOutcome.HasFindings));
        Assert.That(result.findings, Has.Some.Matches<Finding>(finding =>
            finding.severity == CheckSeverity.Blocking
            && finding.title == "Dream Sequence attraction no longer fits"));
    }

    private static string CreateAttraction(string name, float width, float length)
    {
        string path = Root + "/" + name + ".prefab";
        var root = new GameObject(name);
        var attraction = root.AddComponent<AttractionTemplate>();
        attraction.size = GameLevelSize.Custom;
        attraction.customSize = new Vector2(width, length);
        PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return path;
    }

    private static string CreateDream(string sourcePath)
    {
        string path = Root + "/A_DreamSequence.prefab";
        var root = new GameObject("A_DreamSequence");
        var sequence = root.AddComponent<DreamSequenceTemplate>();
        sequence.levels.Add(new DreamSequenceLevel
        {
            sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath),
            displayName = System.IO.Path.GetFileNameWithoutExtension(sourcePath),
        });
        PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return path;
    }

    private static CheckResult Run(string dreamPath)
    {
        var context = new PreUploadCheckContext
        {
            contentId = ContentId,
            contentRoot = Root,
            roots = new List<ContentRootInfo>
            {
                new ContentRootInfo
                {
                    assetPath = dreamPath,
                    name = System.IO.Path.GetFileNameWithoutExtension(dreamPath),
                    guid = AssetDatabase.AssetPathToGUID(dreamPath),
                    kind = ContentRootKindPublic.Attraction,
                },
            },
        };
        return new DreamSequenceRequiredCheck().Run(context);
    }
}
#endif
