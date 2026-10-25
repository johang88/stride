// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio check of an editor extension living in a game project (pass the MorningWood .slnx on the AutoTesting command
// line; MorningWood.Editor, its conversation and quest editors): the plugin is discovered, the editors open, a play-through
// of the test panel follows branches, renaming a node or an objective updates the references (across assets) and undoes,
// new linked cards are undoable, the validator reports a broken link, the quest editor lists the references of the project,
// the property grid offers objective ids. The extension's types are reached with dynamic (the tests don't reference the
// game). Nothing is saved. NARRATIVE_PLAY=1 also starts the game in the conversation (builds the game: slow).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Presentation.Quantum.ViewModels;
using Stride.Core.Presentation.Services;
using Stride.GameStudio.AutoTesting;

namespace Stride.Editor.Tests;

[UITest]
public class NarrativeEditors : IUITest
{
    private readonly List<string> report = new();
    private int failures;

    private void Check(bool condition, string what)
    {
        if (!condition)
            failures++;
        report.Add($"{(condition ? "PASS" : "FAIL")} {what}");
        Console.WriteLine($"NARRATIVE {report[^1]}");
    }

    private void Info(string what)
    {
        report.Add($"INFO {what}");
        Console.WriteLine($"NARRATIVE INFO {what}");
    }

    public async Task Run(IUITestContext ctx)
    {
        var opened = await ctx.WaitForAnyWindow(new[] { GameStudioWindowNames.GameStudio, GameStudioWindowNames.ProjectSelection }, timeoutSeconds: 180);
        if (opened != GameStudioWindowNames.GameStudio)
        {
            ctx.Exit(1);
            return;
        }
        await ctx.SetWindowSize(GameStudioWindowNames.GameStudio, 1920, 1080);
        await ctx.WaitIdle();
        var session = SessionViewModel.Instance;
        var undo = session.ServiceProvider.Get<IUndoRedoService>();
        Task<T> Ui<T>(Func<T> action) => session.Dispatcher.InvokeAsync(action);
        Task Do(Action action) => session.Dispatcher.InvokeAsync(action);

        var plugins = await Ui(() => AssetsPlugin.RegisteredPlugins.Select(x => x.GetType().FullName).ToList());
        Check(plugins.Contains("MorningWood.Editor.NarrativeEditorPlugin"), "narrative editor plugin registered");

        // --- Dialogue editor
        var dialogueUrl = await ctx.OpenAssetEditor("GameData/Dialogues/CaptainHale");
        Check(dialogueUrl != null, "dialogue editor opened");
        await ctx.WaitForRendering(10);
        dynamic editor = await Ui(() => Editor(session, dialogueUrl));
        Check(editor != null && ((object)editor).GetType().Name == "DialogueEditorViewModel", $"dialogue editor type ({((object)editor)?.GetType().Name})");
        if (editor == null)
        {
            Finish(ctx);
            return;
        }
        dynamic data = await Ui(() => (object)editor.Data);
        var nodeCount = await Ui(() => (int)data.Nodes.Count);
        var cardCount = await Ui(() => (int)editor.Nodes.Count);
        Check(cardCount == nodeCount + 1, $"a card per node plus the start ({cardCount} cards, {nodeCount} nodes)");
        var errors = await Ui(() => (int)editor.ErrorCount);
        var problems = await Ui(() => string.Join(" | ", ((IEnumerable)editor.Problems).Cast<dynamic>().Select(x => (string)x.Text)));
        Check(errors == 0, $"no errors in the sample dialogue ({problems})");
        var links = await Ui(() => ((IEnumerable)editor.OutputPorts).Cast<dynamic>().Count(x => x.Target != null));
        Check(links >= 15, $"links drawn ({links})");
        await ctx.CapturePanel(dialogueUrl, "dialogue-editor", 1600, 950);

        // Select a node: its properties in the property grid
        var copies = await Ui(() => AppDomain.CurrentDomain.GetAssemblies().Where(x => x.GetName().Name.StartsWith("MorningWood")).Select(x => $"{x.GetName().Name} @ {x.Location}").ToList());
        Info($"game assemblies loaded: {string.Join(" | ", copies)}");
        Check(copies.Count == copies.Select(x => x.Split(" @ ")[0]).Distinct().Count(), "one copy of each game assembly");
        await Do(() => editor.Reveal((dynamic)Location((object)editor, "report")));
        await ctx.WaitForRendering(20);
        var gridName = await Ui(() => session.AssetViewProperties.Name);
        Check(gridName == "report", $"property grid shows the selected node ({gridName})");
        await ctx.CapturePanel("PropertyGrid", "dialogue-node-properties", 700, 1000);

        // Select the response completing the objective: the property grid offers the quest's objectives
        await Do(() => editor.SelectRow(editor.CardOf("report").Responses[0]));
        await ctx.WaitForRendering(30);
        var choices = await Ui(() => ObjectiveChoices(session));
        Check(choices.Contains("leader") && choices.Contains("report"), $"objective picker offers the quest's objectives ({string.Join(", ", choices)})");
        await ctx.CapturePanel("PropertyGrid", "dialogue-response-properties", 700, 1000);

        // --- Play-through in the test panel: the report with Bram in the party
        await Do(() =>
        {
            editor.IsTestOpen = true;
            var test = editor.Test;
            foreach (dynamic quest in test.Quests)
            {
                quest.State = (dynamic)Enum.Parse(((object)quest.State).GetType(), "Active");
                foreach (dynamic objective in quest.Objectives)
                    objective.Done = objective.Id == "leader" || objective.Id == "insignias";
            }
            foreach (dynamic item in test.Items)
                item.Count = 3;
            foreach (dynamic member in test.Party)
                member.InParty = member.Name == "Bram";
            test.StartCommand.Execute(null);
        });
        await ctx.WaitForRendering(10);
        var first = await Ui(() => Transcript((object)editor).LastOrDefault(x => x.Contains("You're back")));
        Check(first != null, $"play-through starts at the report entry ({string.Join(" / ", await Ui(() => Transcript((object)editor)))})");
        var offered = await Ui(() => ((IEnumerable)editor.Test.Choices).Cast<dynamic>().Where(x => x.IsOffered).Select(x => (string)x.Label).ToList());
        Check(offered.Count == 1 && offered[0].Contains("insignias"), $"only the response whose condition holds is offered ({string.Join(" | ", offered)})");
        var current = await Ui(() => (string)((IEnumerable)editor.Nodes).Cast<dynamic>().FirstOrDefault(x => x.IsCurrent)?.Model.Id);
        Check(current == "report", $"the card of the current node is highlighted ({current})");
        await Do(() => ((IEnumerable)editor.Test.Choices).Cast<dynamic>().First(x => x.IsOffered).Choose.Execute(null));
        await Do(() => editor.Test.ContinueCommand.Execute(null));
        await ctx.WaitForRendering(10);
        var lines = await Ui(() => Transcript((object)editor));
        Check(lines.Any(x => x.Contains("long way round")), "branch on the party: Bram chimes in");
        Check(lines.Any(x => x.Contains("Quest completed")), "completing the last objective completes the quest (simulated)");
        await ctx.CapturePanel(dialogueUrl, "dialogue-editor-test", 1600, 950);
        await Do(() => editor.Test.ContinueCommand.Execute(null));
        await Do(() => editor.Test.ContinueCommand.Execute(null));
        var ended = await Ui(() => !(bool)editor.Test.IsRunning);
        Check(ended, "the play-through ends");

        // --- Rename a node: the links follow, and undo
        await Do(() => editor.CardOf("thanks").Id = "thanks_garrison");
        await ctx.WaitForRendering(5);
        var renamed = await Ui(() => LinksTo((object)data, "thanks_garrison"));
        var stale = await Ui(() => LinksTo((object)data, "thanks"));
        Check(renamed == 2 && stale == 0, $"renaming a node updates the links to it ({renamed} updated, {stale} left)");
        await Do(() => undo.Undo());
        await ctx.WaitForRendering(5);
        var restored = await Ui(() => LinksTo((object)data, "thanks") == 2 && data.FindNode("thanks") != null);
        Check(restored, "undo restores the node id and its links");

        // --- A new line from a response, then undo
        await Do(() =>
        {
            var port = editor.CardOf("active").Responses[0].Port;
            ((IEnumerable)editor.GetDropMenu(port, new System.Windows.Point(2000, 1200))).Cast<dynamic>().First(x => x.Header == "New line").Action();
        });
        await ctx.WaitForRendering(10);
        var created = await Ui(() => (string)data.Nodes[data.Nodes.Count - 1].Id);
        var linked = await Ui(() => (string)editor.CardOf("active").Node.Responses[0].Next);
        Check(created == "line1" && linked == "line1", $"a new line created from a response is linked to it ({created}, {linked})");
        await ctx.CapturePanel(dialogueUrl, "dialogue-editor-new-line", 1600, 950);
        await Do(() => undo.Undo());
        await ctx.WaitForRendering(5);
        var undone = await Ui(() => (int)data.Nodes.Count == nodeCount && (string)editor.CardOf("active").Node.Responses[0].Next == null);
        Check(undone, "undo removes the new line and its link");

        // --- The validator: a broken link
        await Do(() => editor.Edit("Break a link", (Action)(() => editor.SetMember(editor.CardOf("varga").Node, "Next", "nowhere"))));
        await ctx.WaitForRendering(10);
        var broken = await Ui(() => ((IEnumerable)editor.Problems).Cast<dynamic>().Any(x => ((string)x.Text).Contains("'nowhere'")));
        Check(broken, "a link to a missing node is reported");
        await Do(() => undo.Undo());

        // --- Quest editor
        var questUrl = await ctx.OpenAssetEditor("GameData/Quests/BanditsOnTheRoad");
        Check(questUrl != null, "quest editor opened");
        await ctx.WaitForRendering(20);
        dynamic quests = await Ui(() => Editor(session, questUrl));
        Check(quests != null && ((object)quests).GetType().Name == "QuestEditorViewModel", $"quest editor type ({((object)quests)?.GetType().Name})");
        if (quests != null)
        {
            var questDirty = await Ui(() => session.AllAssets.First(x => x.Url == questUrl).IsDirty);
            Check(!questDirty, "opening the quest editor doesn't modify the quest");
            var objectives = await Ui(() => (int)quests.Data.Objectives.Count);
            var questCards = await Ui(() => (int)quests.Nodes.Count);
            Check(questCards == objectives + 2, $"a card per objective plus the quest and its completion ({questCards})");
            var references = await Ui(() => ((IEnumerable)quests.References).Cast<dynamic>().Select(x => $"{x.AssetName}: {x.Summary}").ToList());
            Info($"references: {string.Join(" | ", references)}");
            Check(references.Any(x => x.Contains("Captain Hale") && x.Contains("start")), "references: the dialogue starting the quest");
            Check(references.Any(x => x.Contains("scene")), "references: scene components (triggers, loot, deaths)");
            var completedBy = await Ui(() => ((IEnumerable)quests.CardOf("report").CompletedBy).Cast<dynamic>().Count());
            Check(completedBy >= 1, $"the report objective lists what completes it ({completedBy})");
            var questProblems = await Ui(() => string.Join(" | ", ((IEnumerable)quests.Problems).Cast<dynamic>().Select(x => (string)x.Text)));
            Info($"quest problems: {questProblems}");
            await ctx.CapturePanel(questUrl, "quest-editor", 1600, 950);

            // Rename an objective: the dialogue completing it follows, and undo
            await Do(() => quests.CardOf("report").Id = "report_back");
            await ctx.WaitForRendering(5);
            var objectiveRenamed = await Ui(() => DialogueObjectives((object)data).Contains("report_back") && !DialogueObjectives((object)data).Contains("report"));
            Check(objectiveRenamed, $"renaming an objective updates the dialogue completing it ({string.Join(", ", await Ui(() => DialogueObjectives((object)data)))})");
            var dirty = await Ui(() => session.AllAssets.First(x => x.Url == dialogueUrl).IsDirty);
            Check(dirty, "the dialogue is marked modified by the rename");
            await Do(() => undo.Undo());
            await ctx.WaitForRendering(5);
            var objectiveRestored = await Ui(() => DialogueObjectives((object)data).Contains("report") && quests.CardOf("report") != null);
            Check(objectiveRestored, "undo restores the objective id in both assets");
        }

        // --- Play in game (slow: builds the game)
        if (Environment.GetEnvironmentVariable("NARRATIVE_PLAY") == "1")
        {
            await ctx.OpenAssetEditor("GameData/Dialogues/CaptainHale");
            var task = await Ui(() => (Task)editor.Test.PlayInGame(null));
            await task;
            var status = await Ui(() => (string)editor.Test.Status);
            Info($"play in game: {status}");
            var pid = status?.Contains("process ") == true ? int.Parse(new string(status.Split("process ")[1].TakeWhile(char.IsDigit).ToArray())) : 0;
            Check(pid > 0, "play in game started the game");
            if (pid > 0)
            {
                // The game loads the area and starts the conversation (DialogueTestScenario): still running after a while
                await Task.Delay(30000);
                var process = System.Diagnostics.Process.GetProcessById(pid);
                Check(!process.HasExited, "the game is still running in the conversation");
                if (!process.HasExited)
                    process.Kill();
            }
        }

        Finish(ctx);
    }

    private void Finish(IUITestContext ctx)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "ui-test-out-dpi100");
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, "narrative-report.txt"), report);
        ctx.Exit(failures == 0 ? 0 : 10);
    }

    private static object Editor(SessionViewModel session, string url)
    {
        var asset = session.AllAssets.First(a => a.Url == url);
        return session.ServiceProvider.Get<IAssetEditorsManager>().TryGetAssetEditor<AssetEditorViewModel>(asset, out var editor) ? editor : null;
    }

    /// <summary>
    /// A NarrativeLocation (of the game's design assembly) at a dialogue node.
    /// </summary>
    private static object Location(object editor, string node)
    {
        var type = editor.GetType().GetMethod("Reveal").GetParameters()[0].ParameterType;
        var location = Activator.CreateInstance(type);
        type.GetProperty("NodeId").SetValue(location, node);
        return location;
    }

    private static List<string> Transcript(object target) => Transcript((dynamic)target, 0);

    private static List<string> Transcript(dynamic editor, int unused) => ((IEnumerable)editor.Test.Transcript).Cast<dynamic>().Select(x => (string)x.Text).ToList();

    private static int LinksTo(object target, string id)
    {
        dynamic data = target;
        var count = 0;
        foreach (dynamic node in data.Nodes)
        {
            var type = ((object)node).GetType().Name;
            if ((string)node.Next == id)
                count++;
            if (type == "DialogueBranch")
            {
                foreach (dynamic @case in node.Cases)
                    count += (string)@case.Next == id ? 1 : 0;
            }
            else
            {
                foreach (dynamic response in node.Responses)
                    count += (string)response.Next == id ? 1 : 0;
            }
        }
        return count;
    }

    private static List<string> DialogueObjectives(object target)
    {
        dynamic data = target;
        var result = new List<string>();
        foreach (dynamic node in data.Nodes)
        {
            if (((object)node).GetType().Name == "DialogueBranch")
                continue;
            foreach (dynamic response in node.Responses)
            {
                foreach (dynamic action in response.Actions)
                {
                    if (((object)action).GetType().Name == "CompleteObjectiveAction")
                        result.Add((string)action.Objective);
                }
            }
        }
        return result;
    }

    private static List<string> ObjectiveChoices(SessionViewModel session)
    {
        var root = session.AssetViewProperties.ViewModel?.RootNode;
        var result = new List<string>();
        void Visit(NodeViewModel node)
        {
            if (node.AssociatedData.TryGetValue("ObjectiveChoices", out var choices) && choices is IEnumerable list)
                result.AddRange(list.Cast<object>().Select(x => x.ToString()));
            foreach (var child in node.Children)
                Visit(child);
        }
        if (root != null)
            Visit(root);
        return result.Distinct().ToList();
    }
}
