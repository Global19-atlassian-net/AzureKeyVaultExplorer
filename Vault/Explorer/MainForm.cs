﻿using Microsoft.Azure.KeyVault;
using Microsoft.PS.Common.Vault;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Microsoft.PS.Common.Vault.Explorer
{
    public partial class MainForm : Form
    {
        private Vault _vault;
        private SortOrder _sortOder = SortOrder.Ascending;
        private int _sortColumn = 0;

        public MainForm()
        {
            InitializeComponent();
            uxComboBoxEnv.SelectedIndex = 0;
            uxComboBoxGeo.SelectedIndex = 0;
        }

        private UxOperation NewUxOperation(ToolStripItem controlToToggle) => new UxOperation(controlToToggle, uxStatusLabel);

        private void RefreshSecertsCount()
        {
            uxStatusLabelSecertsCount.Text = $"{uxListViewSecrets.Items.Count} secret(s)";
        }

        private async void uxButtonRefresh_Click(object sender, EventArgs e)
        {
            string geo = ((string)uxComboBoxGeo.SelectedItem).Substring(0, 2);
            string env = (string)uxComboBoxEnv.SelectedItem;

            using (NewUxOperation(uxButtonRefresh))
            {
                _vault = new Vault(geo, env, Utils.GeoRegions[geo]);
                //uxListViewSecrets.BeginUpdate();
                uxListViewSecrets.Items.Clear();
                foreach (var s in await _vault.ListSecretsAsync())
                {
                    uxListViewSecrets.Items.Add(new SecretListViewItem(s));
                }
                //uxListViewSecrets.EndUpdate();
                uxButtonAdd.Enabled = uxMenuItemAdd.Enabled = uxMenuItemAddCertificate.Enabled = true;
                uxImageSearch.Enabled = uxTextBoxSearch.Enabled = true;
                RefreshSecertsCount();
            }
        }

        private void uxListViewSecrets_SelectedIndexChanged(object sender, EventArgs e)
        {
            bool itemSelected = (uxListViewSecrets.SelectedItems.Count == 1);
            bool secretEnabled = itemSelected ? (uxListViewSecrets.SelectedItems[0] as SecretListViewItem).Attributes.Enabled ?? true : false;
            uxButtonEdit.Enabled = uxButtonCopy.Enabled = uxMenuItemEdit.Enabled = uxMenuItemCopy.Enabled = secretEnabled;
            uxButtonDelete.Enabled = uxMenuItemDelete.Enabled = itemSelected;
            uxButtonToggle.Enabled = uxMenuItemToggle.Enabled = itemSelected;
            uxButtonToggle.Text = secretEnabled ? "Disabl&e" : "&Enable";
            uxMenuItemToggle.Text = uxButtonToggle.Text + "...";
            uxPropertyGridSecret.SelectedObject = itemSelected ? uxListViewSecrets.SelectedItems[0] : null;
        }

        private void uxListViewSecrets_KeyUp(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Insert:
                    uxButtonAdd.PerformClick();
                    return;
                case Keys.Delete:
                    uxButtonDelete.PerformClick();
                    return;
            }
            if (!e.Control) return;
            switch (e.KeyCode)
            {
                case Keys.C:
                    uxButtonCopy.PerformClick();
                    return;
                case Keys.E:
                    uxButtonEdit.PerformClick();
                    return;
                case Keys.R:
                    uxButtonRefresh.PerformClick();
                    return;
            }
        }

        private void uxButtonAdd_Click(object sender, EventArgs e)
        {
            (sender as ToolStripDropDownItem)?.ShowDropDown();
        }

        private async Task AddOrUpdateSecret(Secret sOld, SecretObject soNew)
        {
            Secret s = null;
            // New secret, secret rename or new value
            if ((sOld == null) || (sOld.SecretIdentifier.Name != soNew.Name) || (sOld.Value != soNew.RawValue))
            {
                s = await _vault.SetSecretAsync(soNew.Name, soNew.RawValue, soNew.TagsToDictionary(), ContentTypeEnumConverter.GetDescription(soNew.ContentType), soNew.ToSecretAttributes());
            }
            else // Same secret name and value
            {
                s = await _vault.UpdateSecretAsync(soNew.Name, soNew.TagsToDictionary(), ContentTypeEnumConverter.GetDescription(soNew.ContentType), soNew.ToSecretAttributes());
            }
            string oldSecretName = sOld?.SecretIdentifier.Name;
            if ((oldSecretName != null) && (oldSecretName != soNew.Name)) // Delete old key
            {
                await _vault.DeleteSecretAsync(oldSecretName);
                uxListViewSecrets.Items.RemoveByKey(oldSecretName);
            }
            uxListViewSecrets.Items.RemoveByKey(soNew.Name);
            var slvi = new SecretListViewItem(s);
            uxListViewSecrets.Items.Add(slvi);
            uxTimerSearchTextTypingCompleted_Tick(null, EventArgs.Empty); // Refresh search
            slvi.RefreshAndSelect();
            RefreshSecertsCount();
        }

        private async void uxButtonAddItem_Click(object sender, EventArgs e)
        {
            SecretDialog nsDlg = null;
            // Add secret
            if ((sender == uxAddSecret) || (sender == uxMenuItemAddSecret))
            {
                nsDlg = new SecretDialog();
            }
            // Add certificate
            if (((sender == uxAddCertificate) || (sender == uxMenuItemAddCertificate)) && ((uxOpenCertFileDialog.ShowDialog() == DialogResult.OK)))
            {
                nsDlg = new SecretDialog(X509Certificate2.CreateFromCertFile(uxOpenCertFileDialog.FileName));
            }
            // Add configuration file
            if (((sender == uxAddFile) || (sender == uxMenuItemAddFile)) && (uxOpenConfigFileDialog.ShowDialog() == DialogResult.OK))
            {
                FileInfo fi = new FileInfo(uxOpenConfigFileDialog.FileName);
                if (fi.Length > Consts.MaxSecretValueLength)
                {
                    MessageBox.Show($"Configuration file {fi.FullName} size is {fi.Length:N0} bytes. Maximum file size allowed for secret value is {Consts.MaxSecretValueLength:N0} bytes.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                nsDlg = new SecretDialog(fi.FullName);
            }
            if ((nsDlg != null) &&
                (nsDlg.ShowDialog() == DialogResult.OK) &&
                (!uxListViewSecrets.Items.ContainsKey(nsDlg.SecretObject.Name) ||
                (uxListViewSecrets.Items.ContainsKey(nsDlg.SecretObject.Name) && 
                (MessageBox.Show($"Are you sure you want to replace secret '{nsDlg.SecretObject.Name}' with new value?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes))))
            {
                using (NewUxOperation(uxButtonAdd))
                {
                    await AddOrUpdateSecret(null, nsDlg.SecretObject);
                }
            }
        }

        private async void uxButtonEdit_Click(object sender, EventArgs e)
        {
            if (uxListViewSecrets.SelectedItems.Count == 1)
            {
                var slvi = uxListViewSecrets.SelectedItems[0] as SecretListViewItem;
                if (slvi.Attributes.Enabled ?? true)
                {
                    using (NewUxOperation(uxButtonEdit))
                    {
                        var s = await _vault.GetSecretAsync(slvi.Name);
                        SecretDialog nsDlg = new SecretDialog(s);
                        if (nsDlg.ShowDialog() == DialogResult.OK)
                        {
                            await AddOrUpdateSecret(s, nsDlg.SecretObject);
                        }
                    }
                }
            }
        }

        private async void uxButtonToggle_Click(object sender, EventArgs e)
        {
            if (uxListViewSecrets.SelectedItems.Count == 1)
            {
                var slvi = uxListViewSecrets.SelectedItems[0] as SecretListViewItem;
                string action = (slvi.Attributes.Enabled ?? true) ? "disable" : "enable";
                if (MessageBox.Show($"Are you sure you want to {action} secret '{slvi.Name}'?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    using (NewUxOperation(uxButtonToggle))
                    {
                        Secret s = await _vault.UpdateSecretAsync(slvi.Name, Utils.AddChangedBy(slvi.Tags), null, new SecretAttributes() { Enabled = !slvi.Attributes.Enabled }); // Toggle only Enabled attribute
                        slvi = new SecretListViewItem(s);
                        uxListViewSecrets.Items.RemoveByKey(slvi.Name);
                        uxListViewSecrets.Items.Add(slvi);
                        slvi.RefreshAndSelect();
                    }
                }
            }
        }

        private async void uxButtonDelete_Click(object sender, EventArgs e)
        {
            if (uxListViewSecrets.SelectedItems.Count == 1)
            {
                string secretName = uxListViewSecrets.SelectedItems[0].Text;
                if (MessageBox.Show($"Are you sure you want to delete secret '{secretName}'?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    using (NewUxOperation(uxButtonDelete))
                    {
                        await _vault.DeleteSecretAsync(secretName);
                        uxListViewSecrets.Items.RemoveByKey(secretName);
                        RefreshSecertsCount();
                    }
                }
            }
        }

        private void uxTimerSearchTextTypingCompleted_Tick(object sender, EventArgs e)
        {
            uxTimerSearchTextTypingCompleted.Stop();

            SecretListViewItem selectItem = null;
            uxListViewSecrets.BeginUpdate();
            foreach (var item in uxListViewSecrets.Items)
            {
                SecretListViewItem slvi = item as SecretListViewItem;
                bool contains = slvi.Contains(uxTextBoxSearch.Text);
                slvi.Strikeout = !contains;
                if ((selectItem == null) && contains)
                {
                    selectItem = slvi;
                }
            }
            selectItem?.RefreshAndSelect();
            uxListViewSecrets.EndUpdate();
        }


        private void uxTextBoxSearch_TextChanged(object sender, EventArgs e)
        {
            uxTimerSearchTextTypingCompleted.Stop(); // Wait for user to finish the typing in a text box
            uxTimerSearchTextTypingCompleted.Start();
        }

        private async void uxButtonCopy_Click(object sender, EventArgs e)
        {
            if (uxListViewSecrets.SelectedItems.Count == 1)
            {
                string secretName = uxListViewSecrets.SelectedItems[0].Text;
                using (NewUxOperation(uxButtonCopy))
                {
                    var so = new SecretObject(await _vault.GetSecretAsync(secretName), null);
                    Clipboard.SetText(so.Value);
                }
            }
        }

        private void uxButtonHelp_Click(object sender, EventArgs e)
        {
            Process.Start("https://microsoft.visualstudio.com/DefaultCollection/Windows%20Defender/_git/WD.Services.Common?path=%2FVault%2FVaultWiki.md");
        }

        private void uxButtonExit_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void uxListViewSecrets_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (_sortColumn == e.Column)
            {
                _sortOder = (_sortOder == SortOrder.Ascending) ? SortOrder.Descending : SortOrder.Ascending;
            }
            _sortColumn = e.Column;
            uxListViewSecrets.ListViewItemSorter = new ListViewItemComparer(e.Column, _sortOder);
        }
    }
}