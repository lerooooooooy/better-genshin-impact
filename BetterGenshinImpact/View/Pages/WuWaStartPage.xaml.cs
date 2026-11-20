using BetterGenshinImpact.ViewModel.Pages;

namespace BetterGenshinImpact.View.Pages;

public partial class WuWaStartPage
{
    private WuWaStartPageViewModel ViewModel { get; }

    public WuWaStartPage(WuWaStartPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
}
