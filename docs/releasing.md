# Releasing

Releases are built by GitHub Actions (`.github/workflows/release.yml`) when a version tag is pushed. The workflow runs the same `scripts/package.sh` you can run locally.

## Steps

1. In `build.yaml`, raise `version` (four parts, e.g. `0.2.0.0`) and replace `changelog` with this release's changes; it becomes the release notes and the text Jellyfin shows. Add the same entry to `CHANGELOG.md`.
2. Commit, then tag and push:

   ```sh
   git tag v0.2.0.0
   git push origin main v0.2.0.0
   ```

   `v0.2.0` also works for version `0.2.0.0`.

The workflow then:

- checks that the tag matches `build.yaml`;
- builds with warnings as errors and runs every test, with ffmpeg installed and `AUDIOVAULT_REQUIRE_FFMPEG=1`;
- builds `audiovault_<version>.zip` with jprm (which also stamps the version into `Directory.Build.props` for the build) and attaches it to a GitHub release for the tag;
- adds the version to `manifest.json` and commits it to the `gh-pages` branch. The branch only stores the manifest, so earlier versions stay listed;
- deploys that folder to GitHub Pages with `actions/deploy-pages`. A push to `gh-pages` by the workflow doesn't publish anything by itself.

If a run fails part way, rerun it. Each step picks up where the last run stopped:

- the release is created only if it doesn't exist yet, and the zip is uploaded again, replacing the old one;
- the manifest entry for the same version is replaced, not added twice;
- an unchanged `gh-pages` is not committed again.

The repository address users add in Jellyfin is `https://jessicategner.github.io/jellyfin-plugin-audiovault/manifest.json` (in general `https://<owner>.github.io/<repo>/manifest.json`). To use a custom domain, set it under Settings → Pages and set the repository variable `PLUGIN_REPOSITORY_URL` to its base address.

## One-time setup on GitHub

- Settings → Pages → Build and deployment → Source: **GitHub Actions**. The first release creates the `gh-pages` branch; don't delete it, it holds the manifest history.
- Settings → Environments → `github-pages` (created when Pages is set to GitHub Actions): under *Deployment branches and tags*, add a tag rule `v*`. By default only the default branch may deploy, and the release runs on a tag.
- The release workflow asks for `contents: write` (release and `gh-pages`), `pages: write` and `id-token: write` (deploy) itself. If an organisation policy caps workflow permissions at read-only, allow write access under Settings → Actions → General.

## Jellyfin version (targetAbi)

`targetAbi` in `build.yaml` is the oldest Jellyfin that Jellyfin's plugin catalog offers the plugin to and will load it on. It must equal `JellyfinVersion` in `Directory.Build.props` (the Jellyfin.Controller and Jellyfin.Model packages it is built against), written with four parts: `12.2.0` becomes `12.2.0.0`. `scripts/package.sh` and `PackagingTests` both refuse a mismatch. To move to a newer Jellyfin, change both together.

The weekly *Jellyfin compatibility* workflow builds against the newest Jellyfin packages on nuget.org, release candidates included, and checks the Jellyfin internals the plugin relies on against the `12.2`, `latest` and `unstable` server images. When it fails, its log names each internal that no longer binds.

## Local test build

```sh
scripts/package.sh http://localhost:18600
python3 -m http.server 18600 --bind 127.0.0.1 -d artifacts/repo
```

Then add `http://localhost:18600/manifest.json` as a repository in Jellyfin. Nothing is uploaded.
