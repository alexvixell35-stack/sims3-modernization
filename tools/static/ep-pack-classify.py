#!/usr/bin/env python3
"""Tag each type of the docs/586 coverage CSV with the pack it most likely
belongs to (base, EP1-EP11, SP, store) and rank the uncovered EP/SP types
(docs/587).

EP and SP code ships inside Sims3GameplaySystems / Sims3GameplayObjects, so
there is no assembly split. Two signals, first match wins:
  1. a keyword table on the namespace + type name (pack-specific nouns);
  2. the ProductVersion.EPn / SPn references in the type's own file: a type
     whose references all name one pack, at least twice, goes to that pack.
Sims3StoreObjects types are "store". Everything else is "base". The tag is a
ranking aid, not ground truth (shared base types that test for a pack stay
base unless their name says otherwise).

Usage: python3 -I tools/static/ep-pack-classify.py <coverage.csv> <decompiled-root>
           [--extra TSV ...] [--top N]
--extra adds the TSV output of the rea2 scanners (pick-loop, random-empty,
foreach-mutate, managed-lifecycle, unset-field-push): one hit per line that
names a type file. Prints a TSV: pack, class, hits, extra, reach, rank, read.
"""
import argparse, csv, os, re
from collections import Counter

PACKS = [  # (pack, regex on "namespace.Type"); first match wins
    ("EP11", r"TimeTravel|Plumbot|Simbot|BotBuild|Hover|Jetpack|Future|Holo|Nanite|Oracle|Monorail|TimePortal|Teleport|Drone|ServoBot|Laser(?!Harp)|Fluorescent|Ancestor|CarnivalLotus|LaserRhythm|Maglev|SpaceElevator|Robot|Cosmetic.*Prod|Anomaly|Timemachine|ChronoCards|Paradox"),
    ("EP10", r"Island|Resort|Houseboat|Boat|Scuba|Mermaid|Dive|Diving|Lifeguard|Snorkel|Hammock|MarineLife|Shells|Sail|Ferry|Seashell|Treasure|BeachCabana|Beach|Bonfire|Tiki|Lighthouse|Spearfish|Underwater|WaterSki|Jetski|Kayak|Mixology"),
    ("EP9", r"University|Academic|SocialGroup|Rebel|Nerd|Jock|Dorm|Graffiti|Protest|StreetArt|Streaking|Kegs?\b|Keg[A-Z]|BeerPong|JuiceKeg|Influence|Campus|ScienceLab|Lecture|Mascot|Fountain|Tailgat|HackySack|Tutor|ExamMachine|Diploma|Degree|Pillow"),
    ("EP8", r"Season|Weather|Festival|Holiday|Snow|Rain|Lightning|Leaf|Leaves|Pumpkin|IceSkat|Skating|Alien|Football|Umbrella|KissingBooth|Hail|Frost|Temperature|Thermostat|Sled|Igloo|Tanning|TrickOrTreat|Costume|Present|Haunt|Winter|Spring|Summer|Fall[A-Z]|Swim(?!Here)|Soccer|Egg[A-Z]|SpaceRock|Scarecrow|Patio|Pie[A-Z]|Fallen"),
    ("EP7", r"Fairy|Werewolf|Witch|Zombie|Alchemy|Elixir|Spell|Magic|Lunar|Moon|FortuneTell|Wand|Broom|Gnome|Hunting|Occult|Supernatural|Fae|Bonehilda|Mummy|Genie|PlantSim|ImaginaryFriend|Vampire|Ghost|Crystal|Potion|Seance|Lycan|Hex|Cauldron|Toadstool|Grimoire|Faery|Nectar"),
    ("EP6", r"Performance|Singer|Magician|Acrobat|Proprietor|SimPort|Stage|Gig|Spotlight|Busk|Tip[A-Z]|Theater|Show[A-Z]|Showtime|ShowStage|DJ|Karaoke|Signature"),
    ("EP5", r"Pet|Horse|Cat[A-Z]|Cat\b|Dog|Unicorn|Equestrian|Vet|Stable|Saddle|Litter|Feeder|Hunt|Racing|Jump[A-Z]|Kitten|Puppy|Pupp|Collar|Leash|Agility|Raccoon|Deer|Fetch|Chew|Scratch|Bark|Hiss|Groom|Bird|Rodent|Lizard|Turtle|Snake|MinorPet|Wild|Feral|Stray|Hay|Bale|Trough"),
    ("EP4", r"AfterSchool|Afterschool|Prom|Graduation|Midlife|Bachelor|Treehouse|Daycare|Nanny|BoardingSchool|Prank|JungleGym|Wedding|Memor|Tuxedo|Imagin|Rebel|Curfew|SneakOut|Puberty|TeenAge|Bunk|PillowFight|BubbleBlower|PlayTable|TrainTable|BallPit|Bounce|SpinnerRide|Spaceship|Dresser"),
    ("EP3", r"Celebrity|Vampire|Bar[A-Z]|BarTend|Bartend|Mixer|Club|Bouncer|Paparazzi|Elevator|Apartment|Penthouse|Dance|Dancing|HotTub|Hottub|Pool[A-Z].*Party|Bowling|DJBooth|MechanicalBull|Lounge|Plasma|Escalat|Nightlife|CelebritySystem"),
    ("EP2", r"ActiveCareer|Firefight|FireFighting|Stylist|Investigat|PrivateEye|Ghosthunt|GhostHunt|InteriorDesign|Architect|Sculpt|Invent|Nanobot|Laundry|ClothingPile|Clothesline|Washer|Dryer|Tattoo|Consign|Scrap|Simbot|Novelty|Fire[A-Z]|Fireplace|Tattoo|Doctor|Pegasus|BoxOfMystery|Inventing|Chemistry"),
    ("EP1", r"Tomb|Visa|Adventure|Martial|Photograph|Egypt|China|France|Relic|Excavat|DigSite|Sarcophag|TreasureChest|Basecamp|Zen|Nectar|Collectible|Shower.*Asian|Bookshelf.*Tome|Dueling|Sparring|Board[A-Z]*Breaking|Puzzle|Trap|Ancient|Burial|Gem[A-Z]|Meteor|Shrine"),
]
PACK_RE = [(p, re.compile(r)) for p, r in PACKS]
PV = re.compile(r"ProductVersion\.(EP\d+|SP\d+)")


def type_file(dec, cls, asm):
    ns, _, name = cls.rpartition(".")
    return os.path.join(dec, asm, ns, name + ".cs")


def classify(cls, asm, path):
    if asm == "Sims3StoreObjects":
        return "store"
    for pack, rx in PACK_RE:
        if rx.search(cls):
            return pack
    try:
        refs = Counter(PV.findall(open(path, encoding="utf-8", errors="ignore").read()))
    except OSError:
        refs = Counter()
    if len(refs) == 1:
        pack, n = refs.most_common(1)[0]
        if n >= 2:
            return pack
    return "base"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("csv"); ap.add_argument("dec")
    ap.add_argument("--extra", action="append", default=[])
    ap.add_argument("--top", type=int, default=0)
    ap.add_argument("--all", action="store_true", help="include hand-read types")
    a = ap.parse_args()
    extra = Counter()
    for t in a.extra:
        for line in open(t, encoding="utf-8", errors="ignore"):
            for m in re.finditer(r"([\w\.]+)/([\w\.]+)/(\w+)\.cs", line):
                extra[m.group(2) + "." + m.group(3)] += 1
    rows = []
    for r in csv.DictReader(open(a.csv)):
        cls, asm = r["class"], r["assembly"]
        pack = classify(cls, asm, type_file(a.dec, cls, asm))
        if pack == "base":
            continue
        read = r["hand-read"] == "yes"
        if read and not a.all:
            continue
        hits = float(r["hits"] or 0) + extra[cls]
        reach = int(r["reach"] or 0)
        rows.append((hits * (1 + reach), pack, cls, float(r["hits"] or 0), extra[cls], reach, r["methods"], "yes" if read else "no"))
    rows.sort(key=lambda x: (-x[0], -int(x[6] or 0)))
    print("rank\tpack\tclass\thits\textra\treach\tmethods\tread")
    for row in rows[: a.top or None]:
        print("\t".join(str(v) for v in row))


if __name__ == "__main__":
    main()
