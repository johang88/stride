// Copyright (c) Stride contributors (https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Stride.Core.Assets;
using Stride.Core.Extensions;

namespace Stride.Assets.Presentation.Templates
{
    /// <summary>
    /// Interaction logic for DataAssetTypeWindow.xaml
    /// </summary>
    public partial class DataAssetTypeWindow
    {
        private List<Type> UserTypes { get; }
        public List<string> UserTypesNames { get; }

        public DataAssetTypeWindow(string defaultName)
        {
            UserTypes = typeof(IDataAsset).GetInheritedInstantiableTypes().ToList();
            UserTypes.AddRange(typeof(IData<>).GetGenericInstantiableTypes());
            UserTypesNames = UserTypes.Select(t => t.Name).ToList();

            DataContext = this;
            InitializeComponent();

            NameTextBox.Text = defaultName;
        }

        public string AssetName { get; private set; }

        public Type Type { get; private set; }

        private void Validate()
        {
            AssetName = NameTextBox.Text;
            Type = TypeComboBox.SelectedIndex < 0 ? null : UserTypes[TypeComboBox.SelectedIndex];

            if (Type.GetInterfaces().Any(iface => iface.Name == "IData`1"
                && iface.GetGenericTypeDefinition() == typeof(IData<>)))
                Type = typeof(Data<>).MakeGenericType(Type);

            Result = Stride.Core.Presentation.Services.DialogResult.Ok;
            Close();
        }

        private void Cancel()
        {
            AssetName = null;
            Type = null;
            Result = Stride.Core.Presentation.Services.DialogResult.Cancel;
            Close();
        }

        private void ButtonOk(object sender, RoutedEventArgs e)
        {
            Validate();
        }

        private void ButtonCancel(object sender, RoutedEventArgs e)
        {
            Cancel();
        }

        private void TextBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                Validate();
        }
    }
}