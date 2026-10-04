# Publish to GitHub

Suggested repository name: `prairie-bookings`

Description: `Appointment booking system built with C#, ASP.NET Core, EF Core, SQL, and JavaScript. Includes concurrent booking protection and API integration tests.`

Suggested topics: `csharp`, `aspnet-core`, `entity-framework-core`, `sql`, `sqlite`, `appointment-booking`, `portfolio`.

1. Sign in to GitHub and create an empty public repository named `prairie-bookings`. Leave GitHub's README, license, and gitignore options unchecked because these files already exist here.
2. Open PowerShell in this project folder. The original local project folder already has an initial Git commit; skip the first three commands there. If you extracted the source ZIP, run all commands. Substitute your actual GitHub username in the remote URL:

```powershell
git init -b main
git add .
git commit -m "Build appointment booking portfolio MVP"
git remote add origin https://github.com/YOUR_USERNAME/prairie-bookings.git
git push -u origin main
```

If Git asks for your identity, configure your own preferred name and email locally for this repository. Complete authentication through Git's normal sign-in flow. Never paste a token into source files or a chat message.

3. Confirm the repository displays the README and that Actions completes successfully. The SQLite database, build output, and private booking references are excluded from Git.
4. Take a screenshot of the running app for the README and LinkedIn. Avoid showing a real contact address or an active private booking reference. The browser demo used fictional data.
5. Add the repository to LinkedIn's Projects section, then paste the draft from `LINKEDIN.md`, replacing its URL placeholder.

The app is running locally at http://localhost:5080 during this session. From another computer, the repository's setup instructions are needed until a backend deployment is created. GitHub Pages can host static files but cannot run this .NET API.
