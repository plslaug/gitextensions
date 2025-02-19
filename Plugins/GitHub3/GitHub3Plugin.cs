﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Git.hub;
using GitCommands.Config;
using GitCommands.Remotes;
using GitHub3.Properties;
using GitUIPluginInterfaces;
using GitUIPluginInterfaces.RepositoryHosts;
using ResourceManager;

namespace GitHub3
{
    internal static class GitHubLoginInfo
    {
        private static string _username;
        public static string Username
        {
            get
            {
                if (_username == "")
                {
                    return null;
                }

                if (_username != null)
                {
                    return _username;
                }

                try
                {
                    var user = GitHub3Plugin.GitHub.getCurrentUser();
                    if (user != null)
                    {
                        _username = user.Login;
                        ////MessageBox.Show("GitHub username: " + _username);
                        return _username;
                    }
                    else
                    {
                        _username = "";
                    }

                    return null;
                }
                catch
                {
                    return null;
                }
            }
        }

        public static string OAuthToken
        {
            get => GitHub3Plugin.Instance.OAuthToken.ValueOrDefault(GitHub3Plugin.Instance.Settings);
            set
            {
                _username = null;
                GitHub3Plugin.Instance.OAuthToken[GitHub3Plugin.Instance.Settings] = value;
                GitHub3Plugin.GitHub.setOAuth2Token(value);
            }
        }
    }

    [Export(typeof(IGitPlugin))]
    public class GitHub3Plugin : GitPluginBase, IRepositoryHostPlugin
    {
        public static string GitHubAuthorizationRelativeUrl = "authorizations";
        public static string UpstreamConventionName = "upstream";
        public readonly StringSetting GitHubApiEndpoint = new StringSetting("GitHub (Enterprise) API endpoint", "https://api.github.com");
        public readonly StringSetting OAuthToken = new StringSetting("OAuth Token", "");

        private readonly TranslationString _tokenAlreadyExist = new TranslationString("You already have an OAuth token. To get a new one, delete your old one in Plugins > Settings first.");

        internal static GitHub3Plugin Instance;
        internal static Client _gitHub;
        internal static Client GitHub => _gitHub ?? (_gitHub = new Client(Instance.GitHubApiEndpoint.ValueOrDefault(Instance.Settings)));

        private IGitUICommands _currentGitUiCommands;

        public GitHub3Plugin() : base(true)
        {
            SetNameAndDescription("GitHub");
            Translate();

            if (Instance == null)
            {
                Instance = this;
            }

            Icon = Resources.IconGitHub;
        }

        public override IEnumerable<ISetting> GetSettings()
        {
            yield return OAuthToken;
        }

        public override void Register(IGitUICommands gitUiCommands)
        {
            _currentGitUiCommands = gitUiCommands;
            if (!string.IsNullOrEmpty(GitHubLoginInfo.OAuthToken))
            {
                GitHub.setOAuth2Token(GitHubLoginInfo.OAuthToken);
            }
        }

        public override bool Execute(GitUIEventArgs args)
        {
            if (string.IsNullOrEmpty(GitHubLoginInfo.OAuthToken))
            {
                var authorizationApiUrl = new Uri(new Uri(GitHubApiEndpoint.ValueOrDefault(Settings)), GitHubAuthorizationRelativeUrl).ToString();
                using (var gitHubCredentialsPrompt = new GitHubCredentialsPrompt(authorizationApiUrl))
                {
                    gitHubCredentialsPrompt.ShowDialog(args.OwnerForm);
                }
            }
            else
            {
                MessageBox.Show(args.OwnerForm, _tokenAlreadyExist.Text);
            }

            return false;
        }

        // --

        public IReadOnlyList<IHostedRepository> SearchForRepository(string search)
        {
            return GitHub.searchRepositories(search).Select(repo => (IHostedRepository)new GitHubRepo(repo)).ToList();
        }

        public IReadOnlyList<IHostedRepository> GetRepositoriesOfUser(string user)
        {
            return GitHub.getRepositories(user).Select(repo => (IHostedRepository)new GitHubRepo(repo)).ToList();
        }

        public IHostedRepository GetRepository(string user, string repositoryName)
        {
            return new GitHubRepo(GitHub.getRepository(user, repositoryName));
        }

        public IReadOnlyList<IHostedRepository> GetMyRepos()
        {
            return GitHub.getRepositories().Select(repo => (IHostedRepository)new GitHubRepo(repo)).ToList();
        }

        public bool ConfigurationOk => !string.IsNullOrEmpty(GitHubLoginInfo.OAuthToken);

        public string OwnerLogin => GitHub.getCurrentUser()?.Login;

        public async Task<string> AddUpstreamRemoteAsync()
        {
            var gitModule = _currentGitUiCommands.GitModule;
            var hostedRemote = GetHostedRemotesForModule().FirstOrDefault(r => r.IsOwnedByMe);
            if (hostedRemote == null)
            {
                return null;
            }

            var hostedRepository = hostedRemote.GetHostedRepository();
            if (!hostedRepository.IsAFork)
            {
                return null;
            }

            if ((await gitModule.GetRemotesAsync()).Any(r => r.Name == UpstreamConventionName || r.FetchUrl == hostedRepository.ParentReadOnlyUrl))
            {
                return null;
            }

            gitModule.AddRemote(UpstreamConventionName, hostedRepository.ParentReadOnlyUrl);
            return UpstreamConventionName;
        }

        public bool GitModuleIsRelevantToMe()
        {
            return GetHostedRemotesForModule().Count > 0;
        }

        /// <summary>
        /// Returns all relevant github-remotes for the current working directory
        /// </summary>
        public IReadOnlyList<IHostedRemote> GetHostedRemotesForModule()
        {
            var gitModule = _currentGitUiCommands.GitModule;
            return Remotes().ToList();

            IEnumerable<IHostedRemote> Remotes()
            {
                var set = new HashSet<IHostedRemote>();

                foreach (string remote in gitModule.GetRemoteNames())
                {
                    var url = gitModule.GetSetting(string.Format(SettingKeyString.RemoteUrl, remote));

                    if (string.IsNullOrEmpty(url))
                    {
                        continue;
                    }

                    if (new GitHubRemoteParser().TryExtractGitHubDataFromRemoteUrl(url, out var owner, out var repository))
                    {
                        var hostedRemote = new GitHubHostedRemote(remote, owner, repository, url);

                        if (set.Add(hostedRemote))
                        {
                            yield return hostedRemote;
                        }
                    }
                }
            }
        }
    }
}
