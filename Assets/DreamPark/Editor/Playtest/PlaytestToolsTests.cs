#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using DreamPark.Playtest;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public sealed class PlaytestToolsTests
{
    static List<JObject> AllTools(bool allowFixes)
    {
        var tools = PlaytestTools.Definitions().OfType<JObject>().ToList();
        tools.AddRange(PlaytestSession.SessionTools(allowFixes));
        return tools;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ToolDefinitions_AreWellFormedClaudeTools(bool allowFixes)
    {
        foreach (var tool in AllTools(allowFixes))
        {
            string name = tool.Value<string>("name");
            Assert.That(name, Does.Match("^[a-z_]{1,64}$"), "tool name");
            Assert.That(tool.Value<string>("description"), Is.Not.Empty, name);
            var schema = tool["input_schema"] as JObject;
            Assert.That(schema, Is.Not.Null, name);
            Assert.That(schema.Value<string>("type"), Is.EqualTo("object"), name);
            var props = schema["properties"] as JObject;
            Assert.That(props, Is.Not.Null, name);
            foreach (var required in (schema["required"] as JArray ?? new JArray()).Values<string>())
                Assert.That(props.ContainsKey(required), Is.True, $"{name} requires undeclared '{required}'");
        }
    }

    [Test]
    public void ToolNames_AreUnique()
    {
        var names = AllTools(true).Select(t => t.Value<string>("name")).ToList();
        Assert.That(names, Is.Unique);
    }

    [Test]
    public void ApplyFix_IsOnlyOfferedWhenFixesAreAllowed()
    {
        Assert.That(AllTools(false).Any(t => t.Value<string>("name") == "apply_fix"), Is.False);
        Assert.That(AllTools(true).Any(t => t.Value<string>("name") == "apply_fix"), Is.True);
    }

    [Test]
    public void FormatEvents_PutsErrorsFirstAndCountsRepeats()
    {
        var events = new List<PlaytestObserver.Event>
        {
            new PlaytestObserver.Event { kind = "lua print", text = "hello" },
            new PlaytestObserver.Event { kind = "lua error", text = "boom", count = 3 },
        };
        string text = PlaytestObserver.FormatEvents(events);
        Assert.That(text.IndexOf("boom"), Is.LessThan(text.IndexOf("hello")));
        Assert.That(text, Does.Contain("(x3)"));
        Assert.That(PlaytestObserver.FormatEvents(new List<PlaytestObserver.Event>()), Is.EqualTo("(nothing new)"));
    }
}
#endif
