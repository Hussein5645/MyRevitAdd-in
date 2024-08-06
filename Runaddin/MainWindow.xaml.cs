using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ExecutableProject
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            SetDefaultRevitVersion();
            UpdateUI();
        }

        private void SetDefaultRevitVersion()
        {
            foreach (ComboBoxItem item in RevitVersionComboBox.Items)
            {
                if (item.Content.ToString() == "2024")
                {
                    RevitVersionComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        private void UpdateUI()
        {
            string pluginName = "MyRevitPlugin";
            string revitVersion = ((ComboBoxItem)RevitVersionComboBox.SelectedItem)?.Content?.ToString() ?? "2024";
            string addinDirectory = $@"C:\ProgramData\Autodesk\Revit\Addins\{revitVersion}";
            string addinFilePath = Path.Combine(addinDirectory, $"{pluginName}.addin");

            if (File.Exists(addinFilePath))
            {
                InstallButton.Visibility = Visibility.Collapsed;
                DeleteButton.Visibility = Visibility.Visible;
                RepairButton.Visibility = Visibility.Visible;
                StatusTextBlock.Text = "Status: Plugin already installed.";
            }
            else
            {
                InstallButton.Visibility = Visibility.Visible;
                DeleteButton.Visibility = Visibility.Collapsed;
                RepairButton.Visibility = Visibility.Collapsed;
                StatusTextBlock.Text = "Status: Plugin not installed.";
            }
        }

        private void ShowProgressBar(string status)
        {
            StatusTextBlock.Text = status;
            LoadingProgressBar.Visibility = Visibility.Visible;
            InstallButton.IsEnabled = false;
            DeleteButton.IsEnabled = false;
            RepairButton.IsEnabled = false;
        }

        private void HideProgressBar(string status)
        {
            StatusTextBlock.Text = status;
            LoadingProgressBar.Visibility = Visibility.Collapsed;
            InstallButton.IsEnabled = true;
            DeleteButton.IsEnabled = true;
            RepairButton.IsEnabled = true;
            CloseButton.Visibility = Visibility.Visible; // Show the Close button
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close(); // Close the window
        }

        private void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            string pluginName = "MyRevitPlugin";
            string className = "MyRevitPlugin.Class1";
            string description = "test";
            string vendorId = "BUMAR";
            string vendorDescription = "BUMAR,  www.bumar.site/";
            string addInId = "F0876102-D868-4344-8D1F-6044976EB990";
            string revitVersion = ((ComboBoxItem)RevitVersionComboBox.SelectedItem)?.Content?.ToString() ?? "2024";

            ShowProgressBar("Installing plugin...");

            try
            {
                string addinDirectory = $@"C:\ProgramData\Autodesk\Revit\Addins\{revitVersion}";
                string pluginDirectory = Path.Combine(addinDirectory, pluginName);

                if (!Directory.Exists(pluginDirectory))
                {
                    Directory.CreateDirectory(pluginDirectory);
                }

                string assemblyPath = Path.Combine(pluginDirectory, $"{pluginName}.dll");
                string assemblySourcePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{pluginName}.dll");

                if (!File.Exists(assemblySourcePath))
                {
                    StatusTextBlock.Text = "Status: Assembly file not found.";
                    return;
                }

                // Copy the assembly file to the new directory
                File.Copy(assemblySourcePath, assemblyPath, true);

                string addinContent = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<RevitAddIns>
    <AddIn Type=""Application"">
        <Name>{pluginName}</Name>
        <FullClassName>{className}</FullClassName>
        <Text>Lab1PlaceGroup</Text>
        <Description>{description}</Description>
        <VisibilityMode>AlwaysVisible</VisibilityMode>
        <Assembly>{assemblyPath}</Assembly>
        <AddInId>{addInId}</AddInId>
        <VendorId>{vendorId}</VendorId>
        <VendorDescription>{vendorDescription}</VendorDescription>
    </AddIn>
</RevitAddIns>";

                string addinFilePath = Path.Combine(addinDirectory, $"{pluginName}.addin");

                File.WriteAllText(addinFilePath, addinContent);

                HideProgressBar("Plugin installed successfully.");
            }
            catch (Exception ex)
            {
                HideProgressBar($"Error - {ex.Message}");
            }
            finally
            {
                UpdateUI();
               
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            string pluginName = "MyRevitPlugin";
            string revitVersion = ((ComboBoxItem)RevitVersionComboBox.SelectedItem)?.Content?.ToString() ?? "2024";
            string addinDirectory = $@"C:\ProgramData\Autodesk\Revit\Addins\{revitVersion}";
            string addinFilePath = Path.Combine(addinDirectory, $"{pluginName}.addin");
            string assemblyPath = Path.Combine(addinDirectory, pluginName, $"{pluginName}.dll");

            ShowProgressBar("Deleting plugin...");

            try
            {
                // Delete the .addin file
                if (File.Exists(addinFilePath))
                {
                    File.Delete(addinFilePath);
                }

                // Delete the .dll file
                if (File.Exists(assemblyPath))
                {
                    File.Delete(assemblyPath);
                }

                // Delete the plugin directory if empty
                if (Directory.Exists(Path.GetDirectoryName(assemblyPath)) && Directory.GetFiles(Path.GetDirectoryName(assemblyPath)).Length == 0)
                {
                    Directory.Delete(Path.GetDirectoryName(assemblyPath));
                }

                HideProgressBar("Plugin deleted successfully.");
            }
            catch (Exception ex)
            {
                HideProgressBar($"Error - {ex.Message}");
            }
            finally
            {
                UpdateUI();
               
            }
        }

        private void RepairButton_Click(object sender, RoutedEventArgs e)
        {
            ShowProgressBar("Repairing plugin...");
            DeleteButton_Click(sender, e); // Delete the existing plugin
            InstallButton_Click(sender, e); // Reinstall the plugin
        }
    }
}
