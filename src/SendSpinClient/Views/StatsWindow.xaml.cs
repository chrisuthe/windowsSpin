// <copyright file="StatsWindow.xaml.cs" company="SendSpin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Windows;
using SendSpinClient.ViewModels;

namespace SendSpinClient.Views;

/// <summary>
/// Interaction logic for StatsWindow.xaml - the "Stats for Nerds" diagnostic window.
/// </summary>
public partial class StatsWindow : Window
{
    private readonly StatsViewModel _viewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatsWindow"/> class.
    /// </summary>
    /// <param name="viewModel">The view model providing real-time stats.</param>
    public StatsWindow(StatsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // Start polling when window opens
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.StartPolling();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.StopPolling();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
