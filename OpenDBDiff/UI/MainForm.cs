using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Essy.Tools.InputBox;
using OpenDBDiff.Abstractions.Schema;
using OpenDBDiff.Abstractions.Schema.Misc;
using OpenDBDiff.Abstractions.Schema.Model;
using OpenDBDiff.Abstractions.Ui;
using OpenDBDiff.Extensions;
using OpenDBDiff.Settings;
using OpenDBDiff.SqlServer.Schema.Model;
using OpenDBDiff.SqlServer.Ui;
using ScintillaNET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
namespace OpenDBDiff.UI
{
    public partial class MainForm : Form
    {
        private Project ActiveProject;
        private IFront LeftDatabaseSelector;
        private IFront RightDatabaseSelector;
        private IOption Options;
        private List<ISchemaBase> _selectedSchemas = new List<ISchemaBase>();

        private List<IProjectHandler> ProjectHandlers = new List<IProjectHandler>();
        private IProjectHandler ProjectSelectorHandler;

        public MainForm()
        {
            InitializeComponent();

            this.Text = string.Concat(nameof(OpenDBDiff), " v", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString());
        }

        private void StartComparison()
        {
            ProgressForm progress = null;
            string errorLocation = null;
            try
            {
                if ((!String.IsNullOrEmpty(ProjectSelectorHandler.GetSourceDatabaseName()) &&
                     (!String.IsNullOrEmpty(ProjectSelectorHandler.GetDestinationDatabaseName()))))
                {
                    Options = Options ?? this.ProjectSelectorHandler.GetDefaultProjectOptions();
                    var leftGenerator = this.ProjectSelectorHandler.SetSourceGenerator(LeftDatabaseSelector.ConnectionString, Options);
                    var rightGenerator = this.ProjectSelectorHandler.SetDestinationGenerator(RightDatabaseSelector.ConnectionString, Options);
                    IDatabaseComparer databaseComparer = this.ProjectSelectorHandler.GetDatabaseComparer();

                    var leftPair = new KeyValuePair<String, IGenerator>(LeftDatabaseSelector.ToString(), leftGenerator);
                    var rightPair = new KeyValuePair<String, IGenerator>(RightDatabaseSelector.ToString(), rightGenerator);

                    // The progress form will execute the comparer to generate action scripts to migrate the right to the left
                    // Hence, inside the ProgressForm and deeper, right is the origin and left is the destination
                    progress = new ProgressForm(rightPair, leftPair, databaseComparer);
                    progress.ShowDialog(this);
                    if (progress.Error != null)
                    {
                        throw new SchemaException(progress.Error.Message, progress.Error);
                    }

                    txtSyncScript.LexerLanguage = this.ProjectSelectorHandler.GetScriptLanguage();
                    txtSyncScript.ReadOnly = false;
                    errorLocation = "Generating Synchronized Script";
                    txtSyncScript.Text = progress.Destination.ToSqlDiff(this._selectedSchemas).ToSQL();
                    txtSyncScript.ReadOnly = true;
                    txtSyncScript.SetMarginWidth();

                    // Notice again that left is destination, because we generated scripts to migrate the right database to the left.
                    schemaTreeView1.LeftDatabase = progress.Destination;
                    schemaTreeView1.RightDatabase = progress.Origin;

                    schemaTreeView1.OnSelectItem += new SchemaTreeView.SchemaHandler(schemaTreeView1_OnSelectItem);
                    schemaTreeView1_OnSelectItem(schemaTreeView1.SelectedNode);
                    textBox1.Text = progress.Origin.ActionMessage.Message;

                    btnCopy.Enabled = true;
                    btnSaveAs.Enabled = true;
                    btnUpdateAll.Enabled = true;
                }
                else
                    MessageBox.Show(Owner, "Please select a valid connection string", "ERROR", MessageBoxButtons.OK,
                                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (errorLocation == null && progress != null)
                {
                    errorLocation = String.Format("{0} (while {1})", progress.ErrorLocation, progress.ErrorMostRecentProgress ?? "initializing");
                }

                throw new SchemaException("Error " + (errorLocation ?? " Comparing Databases"), ex);
            }
        }

        private void schemaTreeView1_OnSelectItem(string nodeFullName)
        {
            try
            {
                txtNewObject.ReadOnly = false;
                txtOldObject.ReadOnly = false;
                txtDiff.ReadOnly = false;

                txtNewObject.ClearAll();
                txtOldObject.ClearAll();
                txtDiff.ClearAll();

                if (string.IsNullOrEmpty(nodeFullName))
                    return;

                IDatabase database = (IDatabase)schemaTreeView1.LeftDatabase;

                ObjectStatus? status;

                status = database.Find(nodeFullName)?.Status;
                if (status.HasValue && status.Value != ObjectStatus.Drop)
                {
                    txtNewObject.Text = database.Find(nodeFullName).ToSql();
                    txtNewObject.SetMarginWidth();
                    if (database.Find(nodeFullName).Status == ObjectStatus.Original)
                    {
                        btnUpdate.Enabled = false;
                    }
                    else
                    {
                        btnUpdate.Enabled = true;
                    }
                    if (database.Find(nodeFullName).ObjectType == ObjectType.Table)
                    {
                        btnCompareTableData.Enabled = true;
                    }
                    else
                    {
                        btnCompareTableData.Enabled = false;
                    }
                }

                database = (IDatabase)schemaTreeView1.RightDatabase;
                status = database.Find(nodeFullName)?.Status;
                if (status.HasValue && status.Value != ObjectStatus.Create)
                {
                    txtOldObject.Text = database.Find(nodeFullName).ToSql();
                    txtOldObject.SetMarginWidth();
                }
                txtNewObject.ReadOnly = true;
                txtOldObject.ReadOnly = true;

                var diff = (new SideBySideDiffBuilder(new Differ())).BuildDiffModel(txtOldObject.Text, txtNewObject.Text);

                var sb = new StringBuilder();
                DiffPiece newLine, oldLine;
                var markers = new Marker[] { txtDiff.Markers[0], txtDiff.Markers[1], txtDiff.Markers[2], txtDiff.Markers[3] };
                foreach (var marker in markers) marker.Symbol = MarkerSymbol.Background;
                markers[0].SetBackColor(Color.LightGreen); // Imaginary (?)
                markers[1].SetBackColor(Color.LightCyan); // Modified
                markers[2].SetBackColor(Color.LightSalmon); // Deleted
                markers[3].SetBackColor(Color.PeachPuff); // Modified

                var indexes = new List<int>[] { new List<int>(), new List<int>(), new List<int>(), new List<int>() };
                var index = 0;
                for (var i = 0; i < Math.Max(diff.NewText.Lines.Count, diff.OldText.Lines.Count); i++)
                {
                    newLine = i < diff.NewText.Lines.Count ? diff.NewText.Lines[i] : null;
                    oldLine = i < diff.OldText.Lines.Count ? diff.OldText.Lines[i] : null;
                    if (oldLine.Type == ChangeType.Inserted)
                    {
                        sb.AppendLine(" " + oldLine.Text);
                    }
                    else if (oldLine.Type == ChangeType.Deleted)
                    {
                        sb.AppendLine("- " + oldLine.Text);
                        indexes[2].Add(index);
                    }
                    else if (oldLine.Type == ChangeType.Modified)
                    {
                        sb.AppendLine("* " + newLine.Text);
                        indexes[1].Add(index++);
                        sb.AppendLine("* " + oldLine.Text);
                        indexes[3].Add(index);
                    }
                    else if (oldLine.Type == ChangeType.Imaginary)
                    {
                        sb.AppendLine("+ " + newLine.Text);
                        indexes[0].Add(index);
                    }
                    else if (oldLine.Type == ChangeType.Unchanged)
                    {
                        sb.AppendLine("  " + oldLine.Text);
                    }
                    index++;
                }
                txtDiff.Text = sb.ToString();
                txtDiff.SetMarginWidth();
                for (var i = 0; i < 4; i++)
                {
                    foreach (var ind in indexes[i])
                    {
                        txtDiff.Lines[ind].MarkerAdd(i);
                    }
                }
            }
            finally
            {
                txtNewObject.ReadOnly = true;
                txtOldObject.ReadOnly = true;
                txtDiff.ReadOnly = true;
            }
        }

        private void tabControl1_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Refresh script when tab is shown
            if (tabControl1.SelectedIndex != 1)
            {
                return;
            }

            var db = schemaTreeView1.LeftDatabase as IDatabase;
            if (db != null)
            {
                this._selectedSchemas = this.schemaTreeView1.GetCheckedSchemas();
                this.txtSyncScript.ReadOnly = false;
                this.txtSyncScript.Text = db.ToSqlDiff(this._selectedSchemas).ToSQL();
                this.txtSyncScript.ReadOnly = true;
                txtSyncScript.SetMarginWidth();
            }
        }

        private void btnCompareTableData_Click(object sender, EventArgs e)
        {
            TreeView tree = (TreeView)schemaTreeView1.Controls.Find("treeView1", true)[0];
            ISchemaBase selected = (ISchemaBase)tree.SelectedNode.Tag;
            DataCompareForm dataCompare = new DataCompareForm(selected, LeftDatabaseSelector.ConnectionString, RightDatabaseSelector.ConnectionString);
            dataCompare.ShowDialog();
        }

        private void btnCompare_Click(object sender, EventArgs e)
        {
            string errorLocation = "Processing Compare";
            try
            {
                Cursor = Cursors.WaitCursor;
                _selectedSchemas = schemaTreeView1.GetCheckedSchemas();
                StartComparison();
                FillDiffComboBox();
                ShowTreeViewNodesDetailed();


                schemaTreeView1.SetCheckedSchemas(_selectedSchemas);
                errorLocation = "Saving Connections";
                Project.SaveLastConfiguration(LeftDatabaseSelector.ConnectionString, RightDatabaseSelector.ConnectionString);
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                HandleException(errorLocation, ex);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void HandleException(string errorLocation, Exception ex)
        {
            var errorDialog = new ErrorForm(ex);
            errorDialog.ShowDialog(this);
        }

        private void UnloadProjectHandler()
        {
            if (ProjectSelectorHandler != null)
            {
                LeftDatabasePanel.Controls.Remove((Control)LeftDatabaseSelector);
                RightDatabasePanel.Controls.Remove((Control)RightDatabaseSelector);
                ProjectSelectorHandler.Unload();
                ProjectSelectorHandler = null;
            }
        }

        private void LoadProjectHandler(IProjectHandler projectHandler)
        {
            UnloadProjectHandler();
            ProjectSelectorHandler = projectHandler;
            LeftDatabaseSelector = ProjectSelectorHandler.CreateSourceSelector();
            RightDatabaseSelector = ProjectSelectorHandler.CreateDestinationSelector();
            LeftDatabasePanel.Controls.Add(LeftDatabaseSelector.Control);
            RightDatabasePanel.Controls.Add(RightDatabaseSelector.Control);
        }

        private void LoadProjectHandler<T>() where T : IProjectHandler, new()
        {
            var handler = new T();
            LoadProjectHandler(handler);
        }

        private void optSybase_CheckedChanged(object sender, EventArgs e)
        {
            /*if (optSybase.Checked)
            {
                this.mySqlConnectFront2 = new OpenDBDiff.Schema.Sybase.Front.AseConnectFront();
                this.mySqlConnectFront1 = new OpenDBDiff.Schema.Sybase.Front.AseConnectFront();
                this.mySqlConnectFront1.Location = new System.Drawing.Point(5, 19);
                this.mySqlConnectFront1.Name = "mySqlConnectFront1";
                this.mySqlConnectFront1.Size = new System.Drawing.Size(410, 214);
                this.mySqlConnectFront1.TabIndex = 10;
                this.mySqlConnectFront2.Location = new System.Drawing.Point(5, 19);
                this.mySqlConnectFront2.Name = "mySqlConnectFront2";
                this.mySqlConnectFront2.Size = new System.Drawing.Size(410, 214);
                this.mySqlConnectFront2.TabIndex = 10;
                this.mySqlConnectFront1.Visible = true;
                this.mySqlConnectFront2.Visible = true;
                this.groupBox3.Controls.Add((System.Windows.Forms.Control)this.mySqlConnectFront2);
                this.groupBox2.Controls.Add((System.Windows.Forms.Control)this.mySqlConnectFront1);
            }
            else
            {
                this.groupBox2.Controls.Remove((System.Windows.Forms.Control)this.mySqlConnectFront1);
                this.groupBox3.Controls.Remove((System.Windows.Forms.Control)this.mySqlConnectFront2);
            }*/
        }

        private void btnSaveAs_Click(object sender, EventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(saveFileDialog1.FileName) && !string.IsNullOrEmpty(Path.GetDirectoryName(saveFileDialog1.FileName)))
                {
                    saveFileDialog1.InitialDirectory = Path.GetDirectoryName(saveFileDialog1.FileName);
                    saveFileDialog1.FileName = Path.GetFileName(saveFileDialog1.FileName);
                }
                saveFileDialog1.ShowDialog(this);
                if (!String.IsNullOrEmpty(saveFileDialog1.FileName))
                {
                    var db = schemaTreeView1.LeftDatabase as IDatabase;
                    if (db != null)
                    {
                        using (StreamWriter writer = new StreamWriter(saveFileDialog1.FileName, false))
                        {
                            this._selectedSchemas = this.schemaTreeView1.GetCheckedSchemas();
                            writer.Write(db.ToSqlDiff(this._selectedSchemas).ToSQL());
                            writer.Close();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HandleException("Save Script As", ex);
            }
        }

        private void btnCopy_Click(object sender, EventArgs e)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetText(txtSyncScript.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show("An error ocurred while trying to copying the text to the clipboard");
                Trace.WriteLine("ERROR: +" + ex.Message);
            }
        }

        private void btnUpdate_Click(object sender, EventArgs e)
        {
            TreeView tree = (TreeView)schemaTreeView1.Controls.Find("treeView1", true)[0];
            TreeNode dbArm = tree.Nodes[0];
            var sb = new StringBuilder();

            foreach (TreeNode node in dbArm.Nodes)
            {
                if (node.Nodes.Count != 0)
                {
                    foreach (TreeNode subnode in node.Nodes)
                    {
                        if (subnode.Checked)
                        {
                            //ISchemaBase selected = (ISchemaBase)tree.SelectedNode.Tag;
                            ISchemaBase selected = (ISchemaBase)subnode.Tag;

                            IDatabase database = (IDatabase)schemaTreeView1.LeftDatabase;

                            if (database.Find(selected.FullName) != null)
                            {
                                switch (selected.ObjectType)
                                {
                                    case ObjectType.Table:
                                        {
                                            switch (selected.Status)
                                            {
                                                case ObjectStatus.Create: sb.Append(Updater.createNew(selected, RightDatabaseSelector.ConnectionString)); break;
                                                case ObjectStatus.Alter: sb.Append(Updater.alter(selected, RightDatabaseSelector.ConnectionString)); break;
                                                case ObjectStatus.AlterWhitespace: sb.Append(Updater.alter(selected, RightDatabaseSelector.ConnectionString)); break;
                                                default: sb.AppendLine($"Nothing could be found to do for table '{selected.Name}'"); break;
                                            }
                                        }
                                        break;

                                    case ObjectType.StoredProcedure:
                                        {
                                            switch (selected.Status)
                                            {
                                                case ObjectStatus.Create: sb.Append(Updater.createNew(selected, RightDatabaseSelector.ConnectionString)); break;
                                                case ObjectStatus.Alter: sb.Append(Updater.alter(selected, RightDatabaseSelector.ConnectionString)); break;
                                                case ObjectStatus.AlterWhitespace: sb.Append(Updater.alter(selected, RightDatabaseSelector.ConnectionString)); break;
                                                default: sb.AppendLine($"Nothing could be found to do for stored procedure '{selected.Name}'"); break;
                                            }
                                        }
                                        break;

                                    case ObjectType.Function:
                                        {
                                            switch (selected.Status)
                                            {
                                                case ObjectStatus.Create: sb.Append(Updater.createNew(selected, RightDatabaseSelector.ConnectionString)); break;
                                                case ObjectStatus.Alter: sb.Append(Updater.alter(selected, RightDatabaseSelector.ConnectionString)); break;
                                                case ObjectStatus.AlterWhitespace: sb.Append(Updater.alter(selected, RightDatabaseSelector.ConnectionString)); break;
                                                case ObjectStatus.Alter | ObjectStatus.AlterBody: sb.Append(Updater.alter(selected, RightDatabaseSelector.ConnectionString)); break;
                                                default: sb.AppendLine($"Nothing could be found to do for function '{selected.Name}'"); break;
                                            }
                                        }
                                        break;

                                    case ObjectType.View:
                                        {
                                            switch (selected.Status)
                                            {
                                                case ObjectStatus.Create: sb.Append(Updater.createNew(selected, RightDatabaseSelector.ConnectionString)); break;
                                                case ObjectStatus.Alter: sb.Append(Updater.alter(selected, RightDatabaseSelector.ConnectionString)); break;
                                                case ObjectStatus.AlterWhitespace: sb.Append(Updater.alter(selected, RightDatabaseSelector.ConnectionString)); break;
                                                case ObjectStatus.Alter | ObjectStatus.AlterBody: sb.Append(Updater.alter(selected, RightDatabaseSelector.ConnectionString)); break;
                                                default: sb.AppendLine($"Nothing could be found to do for view '{selected.Name}'"); break;
                                            }
                                        }
                                        break;

                                    default:
                                        {
                                            switch (selected.Status)
                                            {
                                                case ObjectStatus.Create: sb.Append(Updater.addNew(selected, RightDatabaseSelector.ConnectionString)); break;
                                                default: sb.AppendLine($"Nothing could be found to do for '{selected.Name}'"); break;
                                            }
                                        }
                                        break;
                                }
                            }
                        }
                    }
                }
            }

            if (sb.Length == 0)
                MessageBox.Show(this, "All successful.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(this, sb.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

            if (Options.Comparison.ReloadComparisonOnUpdate)
            {
                StartComparison();
            }

            btnUpdate.Enabled = false;
        }

        private void btnUpdateAll_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Are you sure you want to update all?", "Confirm update", MessageBoxButtons.OKCancel) == DialogResult.OK)
            {
                TreeView tree = (TreeView)schemaTreeView1.Controls.Find("treeView1", true)[0];
                TreeNode database = tree.Nodes[0];
                var sb = new StringBuilder();
                foreach (TreeNode tn in database.Nodes)
                {
                    foreach (TreeNode inner in tn.Nodes)
                    {
                        if (inner.Tag != null)
                        {
                            ISchemaBase item = (ISchemaBase)inner.Tag;
                            switch (item.Status)
                            {
                                case ObjectStatus.Create: sb.Append(Updater.createNew(item, RightDatabaseSelector.ConnectionString)); break;
                                case ObjectStatus.Alter: sb.Append(Updater.alter(item, RightDatabaseSelector.ConnectionString)); break;
                            }
                        }
                    }
                }

                if (sb.Length == 0)
                    MessageBox.Show(this, "Update successful.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, sb.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                StartComparison();
            }
        }

        private void btnOptions_Click(object sender, EventArgs e)
        {
            Options = Options ?? ProjectSelectorHandler.GetDefaultProjectOptions();
            OptionForm form = new OptionForm(this.ProjectSelectorHandler, Options);
            form.OptionSaved += new OptionControl.OptionEventHandler((option) => Options = option);
            form.ShowDialog(this);
        }

        private void LoadProjectHandlers()
        {
            ProjectHandlers.Clear();
            toolProjectTypes.Items.Clear();

            ProjectHandlers.Add(new SqlServer.Ui.SQLServerProjectHandler());
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            LoadProjectHandlers();
            foreach (var projectHandler in ProjectHandlers)
            {
                toolProjectTypes.Items.Add(projectHandler);
            }

            if (toolProjectTypes.SelectedItem == null && toolProjectTypes.Items.Count > 0)
            {
                toolProjectTypes.SelectedIndex = 0;
            }

            var LastConfiguration = Project.GetLastConfiguration();
            if (LastConfiguration != null)
            {
                if (LeftDatabaseSelector != null)
                    LeftDatabaseSelector.ConnectionString = LastConfiguration.ConnectionStringSource;
                if (RightDatabaseSelector != null)
                    RightDatabaseSelector.ConnectionString = LastConfiguration.ConnectionStringDestination;
            }

            txtNewObject.LexerLanguage = "mssql";
            txtNewObject.ReadOnly = false;
            txtOldObject.LexerLanguage = "mssql";
            txtOldObject.ReadOnly = false;
            txtDiff.LexerLanguage = "mssql";
            txtDiff.ReadOnly = false;
            txtDiff.Margins[0].Width = 20;

            Scintilla[] scintillaControls = new Scintilla[] { txtNewObject, txtOldObject, txtDiff, txtSyncScript };
            foreach (var scintilla in scintillaControls)
            {
                scintilla.InitializeScintillaControls();
            }
            txtSyncScript.Text = "";
            txtSyncScript.SetMarginWidth();
        }

        private void btnSaveProject_Click(object sender, EventArgs e)
        {
            try
            {
                if (ActiveProject == null)
                {
                    ActiveProject = new Project
                    {
                        ConnectionStringSource = ProjectSelectorHandler.GetSourceConnectionString(),
                        ConnectionStringDestination = ProjectSelectorHandler.GetDestinationConnectionString(),
                        ProjectName = String.Format(
                            "[{0}].[{1}] - [{2}].[{3}]",
                            ProjectSelectorHandler.GetSourceServerName(),
                            ProjectSelectorHandler.GetSourceDatabaseName(),
                            ProjectSelectorHandler.GetDestinationServerName(),
                            ProjectSelectorHandler.GetDestinationDatabaseName()
                        ),
                        Options = Options ?? ProjectSelectorHandler.GetDefaultProjectOptions(),
                        Type = Project.ProjectType.SQLServer
                    };

                    var newProjectName = InputBox.ShowInputBox("Enter the project name.", ActiveProject.ProjectName.Trim(), false)?.Trim();

                    if (string.IsNullOrWhiteSpace(newProjectName))
                        return;

                    ActiveProject.ProjectName = newProjectName;
                }
                Project.Upsert(ActiveProject);
            }
            catch (Exception ex)
            {
                HandleException("Saving Project", ex);
            }
        }

        private void btnProject_Click(object sender, EventArgs e)
        {
            try
            {
                var projects = Project.GetAll();
                if (projects.Any())
                {
                    var form = new ListProjectsForm(projects);
                    form.OnSelect += new ListProjectHandler(form_OnSelect);
                    form.OnDelete += new ListProjectHandler(form_OnDelete);
                    form.OnRename += new ListProjectHandler(form_OnRename);
                    form.ShowDialog(this);
                }
                else
                    MessageBox.Show(this, "There are currently no saved projects.", "Projects", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                HandleException("Opening Project", ex);
            }
        }

        private void form_OnRename(Project itemSelected)
        {
            try
            {
                Project.Upsert(itemSelected);
            }
            catch (Exception ex)
            {
                HandleException("Renaming Project", ex);
            }
        }

        private void form_OnDelete(Project itemSelected)
        {
            try
            {
                Project.Delete(itemSelected.Id);
                if ((ActiveProject?.Id).HasValue && ActiveProject.Id == itemSelected.Id)
                {
                    ActiveProject = null;
                    LeftDatabaseSelector.ConnectionString = "";
                    RightDatabaseSelector.ConnectionString = "";
                }
            }
            catch (Exception ex)
            {
                HandleException("Deleting Project", ex);
            }
        }

        private void form_OnSelect(Project itemSelected)
        {
            try
            {
                if (itemSelected != null)
                {
                    ActiveProject = itemSelected;
                    LeftDatabaseSelector.ConnectionString = itemSelected.ConnectionStringSource;
                    RightDatabaseSelector.ConnectionString = itemSelected.ConnectionStringDestination;
                }
            }
            catch (Exception ex)
            {
                HandleException("Selecting Project", ex);
            }
        }

        private void btnNewProject_Click(object sender, EventArgs e)
        {
            LeftDatabaseSelector.ConnectionString = "";
            RightDatabaseSelector.ConnectionString = "";
            ActiveProject = null;
        }

        private void toolProjectTypes_SelectedIndexChanged(object sender, EventArgs e)
        {
            UnloadProjectHandler();
            if (toolProjectTypes.SelectedItem != null)
            {
                var handler = toolProjectTypes.SelectedItem as IProjectHandler;
                LoadProjectHandler(handler);
            }
        }

        private void SwapButton_Click(object sender, EventArgs e)
        {
            var temp = RightDatabaseSelector.Clone() as IFront;
            RightDatabaseSelector.SetSettingsFrom(LeftDatabaseSelector);
            LeftDatabaseSelector.SetSettingsFrom(temp);
        }
        
        private void btnScriptObject_Click(object sender, EventArgs e)
        {
            StringBuilder scriptBuilder = new StringBuilder();

            for (int i = 0; i < txtDiff.Lines.Count; i++)
            {
                string lineText = txtDiff.Lines[i].Text.TrimEnd('\r', '\n');

                if (lineText.StartsWith("+ ") || lineText.StartsWith("- ") || lineText.StartsWith("* "))
                {
                    scriptBuilder.AppendLine(lineText.Substring(2)); // Baþýndaki iþareti çýkar, sadece SQL komutunu al
                }
            }

            // Dosyaya yaz
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string scriptPath = $@"C:\Temp\selected_diff_script_{timestamp}.sql";
            try
            {
                File.WriteAllText(scriptPath, scriptBuilder.ToString());
                MessageBox.Show("Seçilen farklar script dosyasýna yazýldý:\n" + scriptPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Dosya yazýlamadý: " + ex.Message);
            }

            // 2. SQL Server bilgilerini al
            var handler = this.ProjectSelectorHandler as SQLServerProjectHandler;
            if (handler == null)
            {
                MessageBox.Show("SQL Server handler bulunamadý.");
                return;
            }

            // Dinamik olarak Destination bilgilerini al
            string destinationServer = handler.DestinationControl.ServerName;
            string destinationDatabase = handler.DestinationControl.DatabaseName;
            string destinationUserName = handler.DestinationControl.UserName;
            string destinationPassword = handler.DestinationControl.Password;
            bool useWindowsAuthenticationDestination = handler.DestinationControl.UseWindowsAuthentication;

            // SSMS baþlatmak için bilgileri kullan
            string ssmsPath = @"C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE\Ssms.exe";

            // Destination için authentication
            string destinationArguments = $"-S {destinationServer} -d {destinationDatabase} -U {destinationUserName} -P {destinationPassword} -i \"{scriptPath}\" -nosplash";
            // SSMS'i baþlat
            try
            {
                Process.Start(ssmsPath , scriptPath);
                MessageBox.Show(destinationArguments);// sadece destination veritabaný için SSMS'i baþlat
            }
            
            catch (Exception ex)
            {
                MessageBox.Show("SSMS baþlatýlamadý: " + ex.Message);
            }
        }
        private void GetSchemasFromTree(TreeNodeCollection nodes, List<ISchemaBase> result)
        {
            foreach (TreeNode node in nodes)

            {
                MessageBox.Show($"Node Text: {node.Text}, Tag: {node.Tag}");
                if (node.Tag is ISchemaBase schemaBase)
                {
                    if (schemaBase.Status == ObjectStatus.Create || schemaBase.Status == ObjectStatus.Drop)
                    {
                        result.Add(schemaBase);
                        
                    }
                }

                if (node.Nodes.Count > 0)
                {
                    GetSchemasFromTree(node.Nodes, result);
                }
            }
            
        }
        private void ShowTreeViewNodesDetailed()
        {
            string allNodes = "";

            foreach (TreeNode parentNode in schemaTreeView1.Nodes)
            {
                allNodes += $"Baþlýk: {parentNode.Text} (Tag: {(parentNode.Tag != null ? parentNode.Tag.GetType().Name : "null")})\n";

                foreach (TreeNode childNode in parentNode.Nodes)
                {
                    allNodes += $"    Alt: {childNode.Text} (Tag: {(childNode.Tag != null ? childNode.Tag.GetType().Name : "null")})\n";
                }
            }

            if (string.IsNullOrEmpty(allNodes))
            {
                MessageBox.Show("TreeView þu anda boþ!", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(allNodes, "TreeView Detaylý Ýçeriði", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void FillDiffComboBox()
        {
            DiffcomboBox.Items.Clear(); // ComboBox'ý temizliyoruz

            foreach (TreeNode parentNode in schemaTreeView1.Nodes) // Örn: Database adý
            {
                foreach (TreeNode categoryNode in parentNode.Nodes) // Örn: Tables, Views, Procedures
                {
                    bool hasDifference = false;

                    foreach (TreeNode itemNode in categoryNode.Nodes) // Örn: Customers, Orders
                    {
                        if (itemNode.Tag is ISchemaBase schemaBase)
                        {
                            if (schemaBase.Status != ObjectStatus.Original)
                            {
                                hasDifference = true;
                                break; // Bu kategori içinde fark bulundu, devam etmeye gerek yok
                            }
                        }
                    }

                    if (hasDifference)
                    {
                        // Ayný kategori daha önce eklendiyse tekrar ekleme
                        if (!DiffcomboBox.Items.Contains(categoryNode.Text))
                            DiffcomboBox.Items.Add(categoryNode.Text);
                    }
                }
            }

            MessageBox.Show($"ComboBox'a eklenen kategori sayýsý: {DiffcomboBox.Items.Count}");

            if (DiffcomboBox.Items.Count > 0)
            {
                DiffcomboBox.SelectedIndex = 0;
                generateSqlButton.Enabled = true;
            }
            else
            {
                generateSqlButton.Enabled = false;
            }
        }



        private void DiffcomboBox_SelectedIndexChanged_1(object sender, EventArgs e)
        {
            generateSqlButton.Enabled = true;
        }
        private void generateSqlButton_Click(object sender, EventArgs e)
        {
            // ComboBox'tan seçilen baþlýðý al
            var selectedTitle = DiffcomboBox.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selectedTitle))
            {
                MessageBox.Show("Lütfen bir baþlýk seçiniz.");
                return;
            }

            // SQL scriptlerini tutacak liste
            List<string> sqlScripts = new List<string>();

            // TreeView'deki her bir parentNode'u dolaþ
            foreach (TreeNode parentNode in schemaTreeView1.Nodes)
            {
                foreach (TreeNode categoryNode in parentNode.Nodes)
                {
                    // Seçilen baþlýkla eþleþiyorsa iþle
                    if (categoryNode.Text == selectedTitle)
                    {
                        foreach (TreeNode itemNode in categoryNode.Nodes)
                        {
                            if (itemNode.Tag is ISchemaBase schemaItem)
                            {
                                var sql = schemaItem.ToSql();

                                if (!string.IsNullOrWhiteSpace(sql))
                                {
                                    sqlScripts.Add(sql);
                                }
                            }
                        }
                    }
                }
            }

            // Hiç SQL bulunmadýysa kullanýcýya bildir
            if (sqlScripts.Count == 0)
            {
                MessageBox.Show("Seçilen baþlýk için SQL scripti bulunamadý.");
                return;
            }

            // SQL scriptlerini dosyaya yaz
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string filePath = $@"C:\file\generated_{timestamp}.sql";
            try
            {
                System.IO.File.WriteAllLines(filePath, sqlScripts);
                MessageBox.Show("SQL scriptleri baþarýyla dosyaya yazýldý:\n" + filePath);
                System.Diagnostics.Process.Start(@"C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE\Ssms.exe", $"\"{filePath}\"");         
            }
            catch (Exception ex)
            {
                MessageBox.Show("SQL scriptleri yazýlýrken hata oluþtu:\n" + ex.Message);
            }
            

        }



        private void WriteAllSqlToFile()
        {
            // SQL scriptlerini tutacak liste
            List<string> allSqlScripts = new List<string>();

            // TreeView'deki her bir parentNode'u dolaþ
            foreach (TreeNode parentNode in schemaTreeView1.Nodes)
            {
                foreach (TreeNode categoryNode in parentNode.Nodes)
                {
                    foreach (TreeNode itemNode in categoryNode.Nodes)
                    {
                        if (itemNode.Tag is ISchemaBase schemaItem)
                        {
                            // Tüm SQL ifadelerini ekle
                            var sql = schemaItem.ToSql();
                            if (!string.IsNullOrWhiteSpace(sql))
                            {
                                allSqlScripts.Add(sql);
                            }

                            // Fark varsa, farklarý da ekle
                            if (schemaItem.Status != ObjectStatus.Original)
                            {
                                var diffScripts = schemaItem.ToSqlDiff(null);
                                foreach (var script in diffScripts)
                                {
                                    if (script is SQLScript diffScript && !string.IsNullOrWhiteSpace(diffScript.SQL))
                                    {
                                        allSqlScripts.Add(diffScript.SQL);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // SQL scriptlerini dosyaya yaz
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string filePath = $@"C:\file\output_{timestamp}.sql";
            try
            {
                System.IO.File.WriteAllLines(filePath, allSqlScripts);
                MessageBox.Show("SQL ve farklar baþarýyla dosyaya yazýldý:\n" + filePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("SQL scriptleri yazýlýrken hata oluþtu:\n" + ex.Message);
            }
        }

        //private void generateSqlButton_Click(object sender, EventArgs e)
        //{
        //    // ComboBox'tan seçilen baþlýðý al
        //    var selectedTitle = DiffcomboBox.SelectedItem?.ToString();
        //    if (string.IsNullOrEmpty(selectedTitle))
        //    {
        //        MessageBox.Show("Lütfen bir baþlýk seçiniz.");
        //        return;
        //    }

        //    // SQL scriptlerini tutacak liste
        //    List<string> diffScripts = new List<string>();

        //    // TreeView'deki her bir parentNode'u dolaþ
        //    foreach (TreeNode parentNode in schemaTreeView1.Nodes)
        //    {
        //        foreach (TreeNode categoryNode in parentNode.Nodes)
        //        {
        //            // Seçilen baþlýkla eþleþiyorsa iþle
        //            if (categoryNode.Text == selectedTitle)
        //            {
        //                MessageBox.Show("eþleþti");

        //                foreach (TreeNode itemNode in categoryNode.Nodes)
        //                {
        //                    if (itemNode.Tag is ISchemaBase schemaItem)
        //                    {
        //                        // Sadece fark varsa iþleme al
        //                        if (schemaItem.Status != ObjectStatus.Original)
        //                        {
        //                            var scriptList = schemaItem.ToSqlDiff(null);

        //                            // scriptList varsa ve boþ deðilse
        //                            if (scriptList != null && scriptList.Count > 0)
        //                            {
        //                                foreach (var scriptObj in scriptList)
        //                                {
        //                                    if (scriptObj is SQLScript script && !string.IsNullOrWhiteSpace(script.SQL))
        //                                    {
        //                                        diffScripts.Add(script.SQL);
        //                                    }
        //                                }
        //                            }
        //                        }
        //                    }
        //                }
        //            }
        //        }
        //    }

        //    // Hiç fark bulunmadýysa kullanýcýya bildir
        //    if (diffScripts.Count == 0)
        //    {
        //        MessageBox.Show("Seçilen baþlýk için fark bulunamadý.");
        //        return;
        //    }

        //    // SQL scriptlerini dosyaya yaz
        //    string filePath = @"C:\file\file.txt";
        //    try
        //    {
        //        System.IO.File.WriteAllLines(filePath, diffScripts);
        //        MessageBox.Show("SQL scriptleri baþarýyla dosyaya yazýldý:\n" + filePath);
        //    }
        //    catch (Exception ex)
        //    {
        //        MessageBox.Show("SQL scriptleri yazýlýrken hata oluþtu:\n" + ex.Message);
        //    }

        //}





    }
}
