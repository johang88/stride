// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Linq;
using Xunit;
using Stride.Core.Diagnostics;
using Stride.Core.Presentation.View;
using Stride.Core.Presentation.ViewModel;
using Stride.Core.Presentation.ViewModels;

namespace Stride.Core.Presentation.Tests
{
    public class TestLoggerViewModel
    {
        private static LoggerViewModel CreateViewModel() => new(new ViewModelServiceProvider([DispatcherService.Create()]));

        [Fact]
        public void TestClearMessagesOfOneLogger()
        {
            var viewModel = CreateViewModel();
            var first = new LoggerResult();
            first.Warning("first warning");
            first.Info("first info");
            var second = new LoggerResult();
            second.Info("second info");
            second.Error("second error");
            viewModel.AddLoggerWithPast(first);
            viewModel.AddLoggerWithPast(second);
            Assert.Equal(4, viewModel.Messages.Count);
            Assert.True(viewModel.HasWarnings);
            Assert.True(viewModel.HasErrors);

            // The messages of the other logger stay, in order, and so do their flags
            viewModel.ClearMessages(second);
            Assert.Equal(["first warning", "first info"], viewModel.Messages.Select(x => x.Text));
            Assert.True(viewModel.HasWarnings);
            Assert.False(viewModel.HasErrors);

            viewModel.ClearMessages(first);
            Assert.Empty(viewModel.Messages);
            Assert.False(viewModel.HasWarnings);
        }

        [Fact]
        public void TestFlagsFollowAllChanges()
        {
            var viewModel = CreateViewModel();
            var logger = new LoggerResult();
            logger.Error("an error");
            logger.Warning("a warning");
            viewModel.AddLoggerWithPast(logger);
            Assert.True(viewModel.HasErrors);
            Assert.True(viewModel.HasWarnings);

            viewModel.ClearMessages();
            Assert.False(viewModel.HasErrors);
            Assert.False(viewModel.HasWarnings);

            // A logger whose messages were cleared has nothing more to remove
            viewModel.ClearMessages(logger);
            Assert.Empty(viewModel.Messages);
        }
    }
}
