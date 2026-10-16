#if UNITY_EDITOR && !DREAMPARKCORE
using DreamPark;
using NUnit.Framework;
using UnityEngine;

public sealed class DreamParkPackageManifestTests
{
    // Unity saves a null [Serializable] field as an empty instance. The manifest must
    // read an absent package back as absent, or the uploader sends Web an empty
    // Adventure ("adventure.attractions must not be empty") and Core picks a package
    // that has nothing in it.
    [Test]
    public void AbsentRecipesStayAbsentAcrossSerialization()
    {
        var source = ScriptableObject.CreateInstance<DreamParkPackageManifest>();
        var copy = ScriptableObject.CreateInstance<DreamParkPackageManifest>();
        try
        {
            source.arena = new PackageRecipe { kind = "arena" };
            source.adventure = null;
            source.sequence = null;

            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(source), copy);

            Assert.That(copy.arena, Is.Not.Null);
            Assert.That(copy.arena.kind, Is.EqualTo("arena"));
            Assert.That(copy.adventure, Is.Null);
            Assert.That(copy.sequence, Is.Null);
            Assert.That(copy.RecipeFor("adventure"), Is.Null);
        }
        finally
        {
            Object.DestroyImmediate(source);
            Object.DestroyImmediate(copy);
        }
    }
}
#endif
