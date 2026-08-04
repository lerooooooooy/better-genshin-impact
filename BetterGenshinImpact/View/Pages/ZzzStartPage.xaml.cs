using BetterGenshinImpact.ViewModel.Pages;

namespace BetterGenshinImpact.View.Pages;

public partial class ZzzStartPage
{
    private ZzzStartPageViewModel ViewModel { get; }

    public ZzzStartPage(ZzzStartPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
}
