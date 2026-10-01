using System.Windows.Controls;
using Exam.Admin.ViewModels;

namespace Exam.Admin.Views;

public partial class RegistrationSuccessPage : UserControl
{
    public RegistrationSuccessPage(ConnectionViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
