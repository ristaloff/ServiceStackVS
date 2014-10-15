﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using EnvDTE;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TemplateWizard;
using NuGet.VisualStudio;
using ServiceStack;
using ServiceStackVS.Wizards.Annotations;
using IServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace ServiceStackVS.Wizards
{
    public class NodeJsPackageWizard : IWizard
    {
        private const string OutputWindowGuid = "{34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3}";
        private const string ServiceStackVSPackageCmdSetGuid = "5e5ab647-6a69-44a8-a2db-6a324b7b7e6d";
        private List<NpmPackage> npmPackages;

        private uint progressRef = 0;

        private DTE _dte;

        private IVsStatusbar bar;

        private IVsStatusbar StatusBar
        {
            get
            {
                if (bar == null)
                {
                    bar = Package.GetGlobalService(typeof (SVsStatusbar)) as IVsStatusbar;
                }

                return bar;
            }
        }

        //private List<BowerPackage> bowerPackages; 

        /// <summary>
        /// Parses XML from WizardData and installs required npm packages
        /// </summary>
        /// <example>
        /// <![CDATA[
        /// <NodeJSRequirements requiresNpm="true">
        ///     <npm-package id="grunt"/>
        ///     <npm-package id="grunt-cli" />
        ///     <npm-package id="gulp" />
        ///     <npm-package id="bower" />
        /// </NodeJSRequirements>]]>
        /// </example>
        /// <param name="automationObject"></param>
        /// <param name="replacementsDictionary"></param>
        /// <param name="runKind"></param>
        /// <param name="customParams"></param>
        public void RunStarted(object automationObject, Dictionary<string, string> replacementsDictionary,
            WizardRunKind runKind, object[] customParams)
        {
            _dte = (DTE) automationObject;
            string wizardData = replacementsDictionary["$wizarddata$"];
            XElement element = XElement.Parse(wizardData);
            npmPackages =
                element.Descendants()
                    .Where(x => x.Name.LocalName.EqualsIgnoreCase("npm-package"))
                    .Select(x => new NpmPackage {Id = x.Attribute("id").Value})
                    .ToList();

            if (NodePackageUtils.TryRegisterNpmFromDefaultLocation())
            {
                if (!NodePackageUtils.HasBowerOnPath())
                {
                    UpdateStatusMessage("Installing bower...");
                    NodePackageUtils.InstallNpmPackageGlobally("bower");
                }
            }

            //Not needed
            //bowerPackages =
            //    element.Descendants()
            //        .Where(x => x.Name.LocalName.EqualsIgnoreCase("bower-package"))
            //        .Select(x => new BowerPackage {Id = x.Attribute("id").Value})
            //        .ToList();
        }

        private void StartRequiredPackageInstallations(OutputWindowWriter outputWindowWriter)
        {
            try
            {
                // Initialize the progress bar.
                StatusBar.Progress(ref progressRef, 1, "", 0, 0);
                outputWindowWriter.Show();
                for (int index = 0; index < npmPackages.Count; index++)
                {
                    var package = npmPackages[index];
                    UpdateStatusMessage("Installing required NPM package '" + package.Id + "'...");
                    package.InstallGlobally(
                        (sender, args) =>
                        {
                            if (!string.IsNullOrEmpty(args.Data))
                            {
                                string s = Regex.Replace(args.Data, @"[^\u0000-\u007F]", string.Empty);
                                outputWindowWriter.WriteLine(s);
                            }
                        },
                        (sender, args) =>
                        {
                            if (!string.IsNullOrEmpty(args.Data))
                            {
                                string s = Regex.Replace(args.Data, @"[^\u0000-\u007F]", string.Empty);
                                outputWindowWriter.WriteLine(s);
                            }
                        }); //Installs global npm package if missing
                    StatusBar.Progress(ref progressRef, 1, "", Convert.ToUInt32(index),
                        Convert.ToUInt32(npmPackages.Count));
                }
            }
            catch (ProcessException pe)
            {
                MessageBox.Show("An error has occurred during a NPM package installation - " + pe.Message,
                    "An error has occurred during a NPM package installation.",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.DefaultDesktopOnly,
                    false);
                throw new WizardBackoutException("An error has occurred during a NPM package installation.");
            }
            catch (TimeoutException te)
            {
                MessageBox.Show("An NPM install has timed out - " + te.Message,
                    "An error has occurred during a NPM package installation.",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.DefaultDesktopOnly,
                    false);
                throw new WizardBackoutException("An error has occurred during a NPM package installation.");
            }
            catch (Exception e)
            {
                MessageBox.Show("An error has occurred during a NPM package installation." + e.Message,
                    "An error has occurred during a NPM package installation.",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.DefaultDesktopOnly,
                    false);
                throw new WizardBackoutException("An error has occurred during a NPM package installation.");
            }
        }

        private void UpdateStatusMessage(string message)
        {
            int frozen = 1;
            int retries = 0;
            while (frozen != 0 && retries < 10)
            {
                retries++;
                StatusBar.IsFrozen(out frozen);
                if (frozen == 0)
                {
                    StatusBar.SetText(message);
                }
                System.Threading.Thread.Sleep(10);
            }
        }

        public void ProjectFinishedGenerating(Project project)
        {
            //
            var outputWindowPane = _dte.Windows.Item(OutputWindowGuid); //Output window pane
            var _outputWindow = new OutputWindowWriter(ServiceStackVSPackageCmdSetGuid, "ServiceStackVS");
            outputWindowPane.Visible = true;
            string projectPath = project.FullName.Substring(0,
                project.FullName.LastIndexOf("\\", System.StringComparison.Ordinal));
            System.Threading.Tasks.Task.Run(() =>
            {
                StartRequiredPackageInstallations(_outputWindow);
                try
                {
                    if (!NodePackageUtils.HasBowerOnPath())
                    {
                        string appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        string npmFolder = Path.Combine(appDataFolder, "npm");
                        npmFolder.AddToPathEnvironmentVariable();
                    }
                    UpdateStatusMessage("Downloading bower depedencies...");
                    NodePackageUtils.RunBowerInstall(projectPath, (sender, args) =>
                    {
                        if (!string.IsNullOrEmpty(args.Data))
                        {
                            string s = Regex.Replace(args.Data, @"[^\u0000-\u007F]", string.Empty);
                            _outputWindow.WriteLine(s);
                        }
                    }, (sender, args) =>
                    {
                        if (!string.IsNullOrEmpty(args.Data))
                        {
                            string s = Regex.Replace(args.Data, @"[^\u0000-\u007F]", string.Empty);
                            _outputWindow.WriteLine(s);
                        }
                    });
                }
                catch (Exception exception)
                {
                    MessageBox.Show("Bower install failed: " + exception.Message,
                        "An error has occurred during a Bower install.",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error,
                        MessageBoxDefaultButton.Button1,
                        MessageBoxOptions.DefaultDesktopOnly,
                        false);
                }
            }).Wait();

            UpdateStatusMessage("Downloading NPM depedencies...");
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    UpdateStatusMessage("Clearing NPM cache...");
                    NodePackageUtils.NpmClearCache(projectPath);
                    UpdateStatusMessage("Running NPM install...");
                    NodePackageUtils.RunNpmInstall(projectPath,
                        (sender, args) =>
                        {
                            if (!string.IsNullOrEmpty(args.Data))
                            {
                                string s = Regex.Replace(args.Data, @"[^\u0000-\u007F]", string.Empty);
                                _outputWindow.WriteLine(s);
                            }
                        },
                        (sender, args) =>
                        {
                            if (!string.IsNullOrEmpty(args.Data))
                            {
                                string s = Regex.Replace(args.Data, @"[^\u0000-\u007F]", string.Empty);
                                _outputWindow.WriteLine(s);
                            }
                        }, 600);
                    _outputWindow.WriteLine("NPM Install complete");
                    UpdateStatusMessage("Ready");
                    StatusBar.Clear();
                }
                catch (Exception exception)
                {
                    _outputWindow.WriteLine("An error has occurred during an NPM install");
                    _outputWindow.WriteLine("NPM install failed: " + exception.Message);
                }
            });
        }

        public void ProjectItemFinishedGenerating(ProjectItem projectItem)
        {
        }

        public bool ShouldAddProjectItem(string filePath)
        {
            return true;
        }

        public void BeforeOpeningFile(ProjectItem projectItem)
        {
        }

        public void RunFinished()
        {
        }
    }

    public class PackageInstallEventArgs : EventArgs
    {
        public NpmPackage Package { get; set; }
        public bool InstallationComplete { get; set; }
    }
}