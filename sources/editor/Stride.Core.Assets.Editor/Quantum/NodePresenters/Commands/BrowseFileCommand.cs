// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stride.Core.IO;
using Stride.Core.Presentation.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Presentation.Services;

namespace Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands
{
    public class BrowseFileCommand : ChangeValueWithPickerCommandBase
    {
        /// <summary>
        /// The name of this command.
        /// </summary>
        public const string CommandName = "BrowseFile";

        private readonly IDialogService dialogService;
        private readonly IInitialDirectoryProvider initialDirectoryProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="BrowseDirectoryCommand"/> class.
        /// </summary>
        /// <param name="dialogService">The dialog service used to pick the file.</param>
        /// <param name="initialDirectoryProvider">An object that provide the initial directory to use in the picker.</param>
        public BrowseFileCommand(IDialogService dialogService, IInitialDirectoryProvider initialDirectoryProvider = null)
        {
            if (dialogService == null) throw new ArgumentNullException(nameof(dialogService));
            this.dialogService = dialogService;
            this.initialDirectoryProvider = initialDirectoryProvider;
        }

        /// <inheritdoc/>
        public override string Name => CommandName;

        /// <inheritdoc/>
        public override CombineMode CombineMode => CombineMode.AlwaysCombine;

        /// <inheritdoc/>
        public override bool CanAttach(INodePresenter nodePresenter)
        {
            return typeof(UFile).IsAssignableFrom(nodePresenter.Type);
        }

        /// <inheritdoc/>
        protected override async Task<PickerResult> ShowPicker(IReadOnlyCollection<INodePresenter> nodePresenters, object currentValue, object parameter)
        {
            var currentPath = currentValue as UPath;
            if (currentPath is not null)
            {
                if (initialDirectoryProvider is not null)
                {
                    currentPath = initialDirectoryProvider.GetInitialDirectory(currentPath.GetFullDirectory());
                }
            }

            var file = await dialogService.OpenFilePickerAsync(currentPath?.GetFullDirectory());
            var pickerResult = new PickerResult
            {
                ProcessChange = file is not null,
                NewValue = file
            };
            return pickerResult;
        }
    }
}
