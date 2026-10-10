#!/usr/bin/env python3
"""Writes the AudioVault parser fixtures: invented pages shaped like AudioVault's, with made-up titles.

Usage: python3 -I scripts/make_fixtures.py           write the fixtures
       python3 -I scripts/make_fixtures.py --check   fail if the committed fixtures differ from what this writes

The output is the same on every run. When AudioVault changes its markup, change the templates here to match and
run it again. See docs/parser-fixtures.md.
"""
import pathlib
import random
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
FIXTURE_DIR = ROOT / "tests" / "AudioVault.Sources.AudioVault.Tests" / "Fixtures"
SITE = "https://audiovault.net"
TOKEN = "fixture-csrf-token"

FIRST = ["Amber", "Broken", "Cedar", "Distant", "Electric", "Falling", "Golden", "Hollow", "Iron", "Jade", "Kindred",
         "Lonely", "Midnight", "Northern", "Open", "Paper", "Quiet", "Restless", "Silver", "Tangled", "Under", "Velvet",
         "Winter", "Yellow", "Zero"]
SECOND = ["Harbour", "Lanterns", "Orchard", "Signal", "Valley", "Engine", "Letters", "Garden", "Bridge", "Voyage",
          "Echoes", "Kingdom", "Station", "Meadow", "Circuit", "Tides", "Window", "Frontier", "Archive", "Summit"]
TAGS = ["[TTS]", "[UK]", "[US]", "[French Description]", "[New Description]", "[Spanish Description]",
        "[German Description]", "[Director's Cut]"]

# Leading rows in AudioVault's sort order: punctuation and digits before letters.
MOVIE_HEAD = ["'Twas the Night of the Lanterns (2021) [TTS]", "'Til the Tide Turns (2019)", "#PaperHearts (2017)",
              "#Snowbound (2023)", "10.5:  The Last Lighthouse (2006)", "100% Pickle (2020)", "12 Steps 3 - Lockout (2015)",
              "1971: The Year the Radio Sang (2021)"]
SHOW_HEAD = ["#offlineAF - Season 1 (2020)", "'Allo Neighbour - Season 2 (1985)", "007_ Road To Nowhere - Season 2 (2025)",
             "1923 Lane - Season 1 (2022)", "24 Hours in Harbour - Season 3 (2003)", "3rd Moon from the Sun - season 3 (1997)"]


def titles(rng, count, show):
    seen, out = set(), []
    while len(out) < count:
        title = f"{rng.choice(['The ', '', ''])}{rng.choice(FIRST)} {rng.choice(SECOND)}"
        year = rng.randint(1950, 2026)
        if show:
            name = f"{title} - Season {rng.randint(1, 9)} ({year})"
        else:
            name = f"{title} ({year})" + (f" {rng.choice(TAGS)}" if rng.random() < 0.3 else "")
        if name not in seen:
            seen.add(name)
            out.append(name)
    return out


FIXED_IDS = {22977, 14462, 27780, 27781, 10867}


def ids(rng, count):
    return rng.sample([i for i in range(100, 28000) if i not in FIXED_IDS], count)


def row(id_, name, indent):
    pad = " " * indent
    return (f"{pad}<tr>\n{pad}    <td>\n{pad}        {id_}\n{pad}    </td>\n{pad}    <td>\n{pad}        {name}\n{pad}    </td>\n"
            f"{pad}    <td>\n{pad}        <a href=\"{SITE}/download/{id_}\" target=\"_blank\">\n{pad}            Download\n"
            f"{pad}        </a>\n{pad}    </td>\n{pad}</tr>\n")


def table(rows, last_header):
    head = "".join(f"            <th>\n                {h}\n            </th>\n" for h in ["ID", "Name", last_header])
    body = "".join(row(i, n, 8) for i, n in rows)
    return f"<table>\n    <thead>\n        <tr>\n{head}        </tr>\n    </thead>\n    <tbody>\n{body}    </tbody>\n</table>\n"


NAV = f"""<ul>
    <li><a href="{SITE}/">Home</a></li>
    <li><a href="{SITE}/shows">All shows</a></li>
    <li><a href="{SITE}/movies">All movies</a></li>
    <li><a href="mailto:user@example.com">Contact</a></li>
</ul>
"""


def pagination(path, last):
    pages = list(range(1, 9)) + [last - 1, last]
    items = []
    for p in pages:
        if p == 1:
            items.append('    <li class="page-item active" aria-current="page"><span class="page-link">1</span></li>')
        else:
            if p == last - 1:
                items.append('    <li class="page-item disabled" aria-disabled="true"><span class="page-link">...</span></li>')
            items.append(f'    <li class="page-item"><a class="page-link" href="{SITE}/{path}?page={p}">{p}</a></li>')
    items.append(f'    <li class="page-item"><a class="page-link" href="{SITE}/{path}?page=2" rel="next" aria-label="Next &raquo;">&rsaquo;</a></li>')
    return '<ul class="pagination">\n' + "\n".join(items) + "\n</ul>\n"


def listing(heading, path, rows, last):
    return (f"<!DOCTYPE html>\n<html>\n<head>\n<title>Fixture</title>\n</head>\n<body>\n<h2>\n    {heading}\n</h2>\n{NAV}"
            f'<form name="search-date" action="{SITE}/search" method="post">\n    <input type="hidden" name="_token" value="{TOKEN}">\n'
            '    <label>From date:</label>\n    <input type="date" name="date" value="">\n    <button type="submit">Search by date</button>\n</form>\n'
            '<form name="search" action="" method="get">\n    <input type="text" name="search" value="">\n    <button type="submit">Search</button>\n</form>\n'
            + table(rows, "Download") + pagination(path, last) + "</body>\n</html>\n")


def home(shows, movies):
    return (f"<!DOCTYPE html>\n<html>\n<head>\n<title>Fixture</title>\n</head>\n<body>\n<h3>\n    Welcome\n</h3>\n"
            f'<a href="{SITE}/login">Login</a> | <a href="{SITE}/register">Register</a>\n'
            "<p>\n    An invented page with the same structure as the home page.\n</p>\n"
            f"<h2>News</h2>\n<div>\n    <a href=\"{SITE}/news\"><h3 class=\"h5\">An invented news item</h3></a>\n</div>\n"
            "<h2>Featured</h2>\n<div>\n    <p>Nothing featured.</p>\n</div>\n"
            "<div>\n<h5>\n    Counts:\n</h5>\n<p>\n    Downloaded: 1234\n</p>\n</div>\n<hr>\n"
            "<h5>\n    Recent Shows:\n</h5>\n\n" + table(shows, "Actions")
            + "\n<h5>\n    Recent Movies:\n</h5>\n\n" + table(movies, "Actions") + "</body>\n</html>\n")


LOGIN = f"""<form method="POST" action="{SITE}/login">
    <input type="hidden" name="_token" value="{TOKEN}">
    <label for="email">E-Mail Address</label>
    <input id="email" type="email" name="email" value="" required autofocus />
    <label for="password">Password</label>
    <input id="password" type="password" name="password" required />
    <input type="checkbox" name="remember" id="remember">
    <label for="remember">Remember Me</label>
    <button type="submit">Login</button>
</form>
<a href="{SITE}/register">Register</a>
"""


def build():
    rng = random.Random(20261010)
    movies = MOVIE_HEAD + sorted(titles(rng, 100 - len(MOVIE_HEAD), show=False), key=str.lower)
    shows = SHOW_HEAD + sorted(titles(rng, 100 - len(SHOW_HEAD), show=True), key=str.lower)
    # The tests refer to these IDs: 14462 is the second movie, 27780 a recent movie.
    movie_ids = [22977, 14462] + ids(rng, 98)
    recent_shows = list(zip([27781] + ids(rng, 39), titles(rng, 40, show=True)))
    recent_movies = list(zip([27780] + ids(rng, 39), titles(rng, 40, show=False)))
    return {
        "movies_page1.html": listing("View Movies", "movies", list(zip(movie_ids, movies)), 143),
        "shows_page1.html": listing("View Shows", "shows", list(zip([10867] + ids(rng, 99), shows)), 119),
        "home.html": home(recent_shows, recent_movies),
        "login.html": LOGIN,
    }


def main(args):
    files = build()
    if args == ["--check"]:
        stale = [n for n, text in files.items() if (FIXTURE_DIR / n).read_text(encoding="utf-8") != text]
        if stale:
            print("Out of date: " + ", ".join(stale) + ". Run python3 -I scripts/make_fixtures.py.", file=sys.stderr)
            return 1
        print(f"All {len(files)} fixtures are current.")
        return 0
    if args:
        print(__doc__, file=sys.stderr)
        return 2
    for name, text in files.items():
        (FIXTURE_DIR / name).write_text(text, encoding="utf-8")
    print(f"Wrote {len(files)} fixtures to {FIXTURE_DIR.relative_to(ROOT)}.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
