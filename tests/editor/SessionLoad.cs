// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// Session open timing (pass a project .sln/.slnx on the AutoTesting command line): waits for the main window
// with the session loaded, logs how long that took since the process started and how many assets loaded, checks that
// asset menu commands (created on demand) are there, exits. Nothing is saved. Run it under a profiler (dotnet-trace,
// attached with -p after launch: launching under it hangs Game Studio) to look at session load cost.
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Presentation.ViewModel;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.GameStudio.AutoTesting;

namespace Stride.Editor.Tests;

[UITest]
public class SessionLoad : IUITest
{
    public async Task Run(IUITestContext ctx)
    {
        var opened = await ctx.WaitForAnyWindow(new[] { GameStudioWindowNames.GameStudio, GameStudioWindowNames.ProjectSelection }, timeoutSeconds: 600);
        if (opened != GameStudioWindowNames.GameStudio)
        {
            Console.WriteLine("SESSION FAIL no session window");
            ctx.Exit(1);
            return;
        }
        var windowSeconds = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds;
        // STRIDE_SESSION_LOAD_NO_IDLE=1: exit at the main window (a shorter profile, without the editors opening)
        if (Environment.GetEnvironmentVariable("STRIDE_SESSION_LOAD_NO_IDLE") != "1")
            await ctx.WaitIdle();
        var idleSeconds = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds;
        var session = SessionViewModel.Instance;
        var assets = await session.Dispatcher.InvokeAsync(() => session.AllAssets.Count());

        // An asset's menu commands are created when the view first asks for them: a model's has its own and the base ones
        var modelCommands = await session.Dispatcher.InvokeAsync(() => session.AllAssets.OfType<ModelViewModel>().FirstOrDefault()?.AssetCommands.Select(x => x.DisplayName).ToList());
        if (modelCommands is not null && (!modelCommands.Contains("Create Skeleton") || !modelCommands.Contains("Create derived asset")))
        {
            Console.WriteLine($"SESSION FAIL model asset commands: {string.Join(", ", modelCommands)}");
            ctx.Exit(1);
            return;
        }

        Console.WriteLine($"SESSION PASS {assets} assets; main window after {windowSeconds:F1} s, idle after {idleSeconds:F1} s; model commands: {string.Join(", ", modelCommands ?? [])}");
        ctx.Exit(0);
    }
}
