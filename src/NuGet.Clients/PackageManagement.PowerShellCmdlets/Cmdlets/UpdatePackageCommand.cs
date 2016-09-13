﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Packaging.Core;
using NuGet.ProjectManagement;
using NuGet.Resolver;
using NuGet.Versioning;

namespace NuGet.PackageManagement.PowerShellCmdlets
{
    [Cmdlet(VerbsData.Update, "Package", DefaultParameterSetName = "All")]
    public class UpdatePackageCommand : PackageActionBaseCommand
    {
        private ResolutionContext _context;
        private UninstallationContext _uninstallcontext;
        private string _id;
        private string _projectName;
        private bool _idSpecified;
        private bool _projectSpecified;
        private bool _versionSpecifiedPrerelease;
        private bool _allowPrerelease;
        private NuGetVersion _nugetVersion;

        [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true, Position = 0, ParameterSetName = "Project")]
        [Parameter(ValueFromPipelineByPropertyName = true, Position = 0, ParameterSetName = "All")]
        [Parameter(ValueFromPipelineByPropertyName = true, Position = 0, ParameterSetName = "Reinstall")]
        public override string Id
        {
            get { return _id; }
            set
            {
                _id = value;
                _idSpecified = true;
            }
        }

        [Parameter(Position = 1, ValueFromPipelineByPropertyName = true, ParameterSetName = "All")]
        [Parameter(Position = 1, ValueFromPipelineByPropertyName = true, ParameterSetName = "Project")]
        [Parameter(Position = 1, ValueFromPipelineByPropertyName = true, ParameterSetName = "Reinstall")]
        public override string ProjectName
        {
            get { return _projectName; }
            set
            {
                _projectName = value;
                _projectSpecified = true;
            }
        }

        [Parameter(Position = 2, ParameterSetName = "Project")]
        [ValidateNotNullOrEmpty]
        public override string Version { get; set; }

        [Parameter]
        [Alias("ToHighestPatch")]
        public SwitchParameter Safe { get; set; }

        [Parameter]
        public SwitchParameter ToHighestMinor { get; set; }

        [Parameter(Mandatory = true, ParameterSetName = "Reinstall")]
        [Parameter(ParameterSetName = "All")]
        public SwitchParameter Reinstall { get; set; }

        private List<NuGetProject> Projects { get; set; }

        public bool IsVersionEnum { get; set; }

        protected override void Preprocess()
        {
            base.Preprocess();
            ParseUserInputForVersion();
            if (!_projectSpecified)
            {
                Projects = VsSolutionManager.GetNuGetProjects().ToList();
            }
            else
            {
                Projects = new List<NuGetProject> { Project };
            }

            if (Reinstall)
            {
                ActionType = NuGetActionType.Reinstall;
            }
            else
            {
                ActionType = NuGetActionType.Update;
            }
        }

        protected override void ProcessRecordCore()
        {
            Preprocess();

            SubscribeToProgressEvents();
            PerformPackageUpdatesOrReinstalls();
            UnsubscribeFromProgressEvents();
        }

        /// <summary>
        /// Perform package updates or reinstalls
        /// </summary>
        private void PerformPackageUpdatesOrReinstalls()
        {
            // Update-Package without ID specified
            if (!_idSpecified)
            {
                Task.Run(() => UpdateOrReinstallAllPackages());
            }
            // Update-Package with Id specified
            else
            {
                Task.Run(() => UpdateOrReinstallSinglePackage());
            }
            WaitAndLogPackageActions();
        }

        /// <summary>
        /// Update or reinstall all packages installed to a solution. For Update-Package or Update-Package -Reinstall.
        /// </summary>
        /// <returns></returns>
        private async Task UpdateOrReinstallAllPackages()
        {
            try
            {
                // if the source is explicitly specified we will use exclusively that source otherwise use ALL enabled sources
                var actions = await PackageManager.PreviewUpdatePackagesAsync(
                    Projects,
                    ResolutionContext,
                    this,
                    PrimarySourceRepositories,
                    PrimarySourceRepositories,
                    Token);

                await ExecuteActions(actions);
            }
            catch (Exception ex)
            {
                Log(MessageLevel.Error, ExceptionUtilities.DisplayMessage(ex));
            }
            finally
            {
                BlockingCollection.Add(new ExecutionCompleteMessage());
            }
        }

        /// <summary>
        /// Update or reinstall a single package installed to a solution. For Update-Package -Id or Update-Package -Id
        /// -Reinstall.
        /// </summary>
        /// <returns></returns>
        private async Task UpdateOrReinstallSinglePackage()
        {
            try
            {
                if (!await PreviewAndExecuteUpdateActionsforSinglePackage())
                {
                    Log(MessageLevel.Error, Resources.Cmdlet_PackageNotInstalledInAnyProject, Id);
                }
            }
            catch (Exception ex)
            {
                Log(MessageLevel.Error, ExceptionUtilities.DisplayMessage(ex));
            }
            finally
            {
                BlockingCollection.Add(new ExecutionCompleteMessage());
            }
        }

        /// <summary>
        /// Preview update actions for single package
        /// </summary>
        /// <returns></returns>
        private async Task<bool> PreviewAndExecuteUpdateActionsforSinglePackage()
        {
            var actions = Enumerable.Empty<NuGetProjectAction>();

            // Check if the package is installed or not.
            // Used to throw an error message indicating that the package was not installed to begin with.
            var isInstalled = await IsPackageInstalled(Id);

            // If -Version switch is specified
            if (!string.IsNullOrEmpty(Version))
            {
                actions = await PackageManager.PreviewUpdatePackagesAsync(
                    new PackageIdentity(Id, PowerShellCmdletsUtility.GetNuGetVersionFromString(Version)),
                    Projects,
                    ResolutionContext,
                    this,
                    PrimarySourceRepositories,
                    EnabledSourceRepositories,
                    Token);
            }
            else
            {
                actions = await PackageManager.PreviewUpdatePackagesAsync(
                    Id,
                    Projects,
                    ResolutionContext,
                    this,
                    PrimarySourceRepositories,
                    EnabledSourceRepositories,
                    Token);
            }

            await ExecuteActions(actions);
            return isInstalled;
        }

        /// <summary>
        /// Method checks if the package to be updated is installed in any package or not.
        /// </summary>
        /// <param name="packageId">Id of the package to be updated/checked</param>
        /// <returns><code>bool</code> indicating wether the package is installed on any package or not</returns>
        private async Task<bool> IsPackageInstalled(string packageId)
        {
            foreach (var project in Projects)
            {
                var installedPackages = await project.GetInstalledPackagesAsync(CancellationToken.None);
                if (installedPackages.Select(installedPackage => installedPackage.PackageIdentity.Id)
                                     .Any(installedPackageId => installedPackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
                //if (installedPackages.Any(package => StringComparer.Ordinal.Equals(package.PackageIdentity.Id, packageId)))
                //{
                //    return true;
                //}
            }
            return false;
        }

        /// <summary>
        /// Execute the project actions
        /// </summary>
        /// <param name="actions"></param>
        /// <returns></returns>
        private async Task ExecuteActions(IEnumerable<NuGetProjectAction> actions)
        {
            if (!ShouldContinueDueToDotnetDeprecation(actions, WhatIf.IsPresent))
            {
                return;
            }

            if (WhatIf.IsPresent)
            {
                // For -WhatIf, only preview the actions
                PreviewNuGetPackageActions(actions);
            }
            else
            {
                // Execute project actions by Package Manager
                await PackageManager.ExecuteNuGetProjectActionsAsync(Projects, actions, this, Token);
            }
        }

        /// <summary>
        /// Parse user input for -Version switch
        /// </summary>
        private void ParseUserInputForVersion()
        {
            if (!string.IsNullOrEmpty(Version))
            {
                // If Version is prerelease, automatically allow prerelease (i.e. append -Prerelease switch).
                _nugetVersion = PowerShellCmdletsUtility.GetNuGetVersionFromString(Version);
                if (_nugetVersion.IsPrerelease)
                {
                    _versionSpecifiedPrerelease = true;
                }
            }
            _allowPrerelease = IncludePrerelease.IsPresent || _versionSpecifiedPrerelease;
        }

        /// <summary>
        /// Resolution Context for Update-Package command
        /// </summary>
        public ResolutionContext ResolutionContext
        {
            get
            {
                // ResolutionContext contains a cache, this should only be created once per command
                if (_context == null)
                {
                    _context = new ResolutionContext(GetDependencyBehavior(), _allowPrerelease, false, DetermineVersionConstraints());
                }

                return _context;
            }
        }

        /// <summary>
        /// Uninstallation Context for Update-Package -Reinstall command
        /// </summary>
        public UninstallationContext UninstallContext
        {
            get
            {
                _uninstallcontext = new UninstallationContext(false, Reinstall.IsPresent);
                return _uninstallcontext;
            }
        }

        /// <summary>
        /// Return dependecy behavior for Update-Package command.
        /// </summary>
        /// <returns></returns>
        protected override DependencyBehavior GetDependencyBehavior()
        {
            // Return DependencyBehavior.Highest for Update-Package
            if (!_idSpecified
                && !Reinstall.IsPresent)
            {
                return DependencyBehavior.Highest;
            }

            return base.GetDependencyBehavior();
        }

        /// <summary>
        /// Determine the UpdateConstraints based on the command line arguments
        /// </summary>
        private VersionConstraints DetermineVersionConstraints()
        {
            if (Reinstall.IsPresent)
            {
                return VersionConstraints.ExactMajor | VersionConstraints.ExactMinor | VersionConstraints.ExactPatch | VersionConstraints.ExactRelease;
            }
            else if (Safe.IsPresent)
            {
                return VersionConstraints.ExactMajor | VersionConstraints.ExactMinor;
            }
            else if (ToHighestMinor.IsPresent)
            {
                return VersionConstraints.ExactMajor;
            }
            else
            {
                return VersionConstraints.None;
            }
        }
    }
}