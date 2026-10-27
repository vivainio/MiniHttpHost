# Building this book

The book uses [Zensical](https://zensical.org/) with the same pinned version and
GitHub Pages deployment pattern as [roto](https://vivainio.github.io/roto/).
The documentation source lives in `docs/`; navigation and theme live in
`zensical.toml`.

## Local preview

From the repository root, use a Python virtual environment:

```sh
python3 -m venv .venv-docs
.venv-docs/bin/python -m pip install -r requirements-docs.txt
.venv-docs/bin/zensical serve
```

Open <http://localhost:8000/MiniHttpHost/>. On Windows, the corresponding virtual
environment executables are in `.venv-docs\Scripts\`.

## Validate and build

```sh
.venv-docs/bin/zensical build --clean --strict
```

The output goes into `site/`, which is not committed. Strict mode makes broken
internal documentation links and other build warnings fail validation. Add a
navigation entry in `zensical.toml` when adding a chapter.

## GitHub Actions

The [documentation workflow](https://github.com/vivainio/MiniHttpHost/blob/main/.github/workflows/docs.yml)
builds changed documentation on pull requests without deploying it. A push to
`main` builds strictly, uploads the Pages artifact, and deploys it with GitHub's
Pages action. The workflow can also be started manually.

Only the deployment job receives `pages: write` and `id-token: write` permissions.
GitHub Pages is configured to publish through Actions rather than from a branch.
See Zensical's [publishing guide](https://zensical.org/docs/publish-your-site/).
