using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using QuizGame.Host.ViewModels;

namespace QuizGame.Host.Views;

public partial class QuestionBankView : UserControl
{
    public QuestionBankView()
    {
        InitializeComponent();
    }

    private void QuestionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsClickOnDataGridRow(e.OriginalSource as DependencyObject))
            return;

        if (DataContext is QuestionBankViewModel vm && vm.EditQuestionCommand.CanExecute(null))
            vm.EditQuestionCommand.Execute(null);
    }

    private static bool IsClickOnDataGridRow(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is DataGridRow)
                return true;
            if (current is DataGrid)
                return false;

            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
