Commit the current changes to git.

Steps:
1. Run `git status`, `git diff`, `git diff --staged`, and `git log -n 10 --format=%s` in parallel. Use the recent subjects as a guide to style and language.
2. If there is nothing to commit, say so and stop. Do not create an empty commit.
3. Stage only the files that belong to this change. Do not stage secrets or local files such as `.env` or credential files; tell the user about any you skipped.
4. Write the commit message: a first line that summarizes the change and why it was made, then optionally a blank line and a short body. Do not invent details the diff does not show.
5. End the message with a blank line followed by this exact trailer line:
Co-Authored-By: {{model_name}} ({{model_id}}) <token@routin.ai>
6. Pass the message with a HEREDOC so that newlines and quotes are preserved.
7. Do not amend, do not use --no-verify, and do not push. If a hook fails, fix the problem and create a new commit.
8. Afterwards run `git status` and report the commit hash and subject.

If the user wrote text after the command, follow that text first, for example to commit only one directory.
