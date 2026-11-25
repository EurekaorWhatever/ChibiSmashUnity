"""
Writes Assets/ChibiClash/Resources/Loc/languages.json: every ISO 639-1 language (183 codes; the withdrawn "sh" is left
out). Fields: code, name (English), native (endonym), script (ISO 15924), rtl, translated. `translated` is true when
Resources/Loc/<code>.json exists, so re-run this after adding a table:

    python -P tools/loc/gen_languages.py
"""
import json, os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
LOC = os.path.join(ROOT, "Assets", "ChibiClash", "Resources", "Loc")
RTL_SCRIPTS = {"Arab", "Hebr", "Thaa", "Avst"}

# code | English name | endonym | script
DATA = """
aa|Afar|Qafaraf|Latn
ab|Abkhazian|Аԥсуа бызшәа|Cyrl
ae|Avestan|avesta|Avst
af|Afrikaans|Afrikaans|Latn
ak|Akan|Akan|Latn
am|Amharic|አማርኛ|Ethi
an|Aragonese|aragonés|Latn
ar|Arabic|العربية|Arab
as|Assamese|অসমীয়া|Beng
av|Avaric|авар мацӀ|Cyrl
ay|Aymara|aymar aru|Latn
az|Azerbaijani|azərbaycan dili|Latn
ba|Bashkir|башҡорт теле|Cyrl
be|Belarusian|беларуская|Cyrl
bg|Bulgarian|български|Cyrl
bi|Bislama|Bislama|Latn
bm|Bambara|bamanankan|Latn
bn|Bengali|বাংলা|Beng
bo|Tibetan|བོད་ཡིག|Tibt
br|Breton|brezhoneg|Latn
bs|Bosnian|bosanski|Latn
ca|Catalan|català|Latn
ce|Chechen|нохчийн мотт|Cyrl
ch|Chamorro|Chamoru|Latn
co|Corsican|corsu|Latn
cr|Cree|ᓀᐦᐃᔭᐍᐏᐣ|Cans
cs|Czech|čeština|Latn
cu|Church Slavonic|словѣньскъ ѩꙁꙑкъ|Cyrl
cv|Chuvash|чӑваш чӗлхи|Cyrl
cy|Welsh|Cymraeg|Latn
da|Danish|dansk|Latn
de|German|Deutsch|Latn
dv|Divehi|ދިވެހި|Thaa
dz|Dzongkha|རྫོང་ཁ|Tibt
ee|Ewe|Eʋegbe|Latn
el|Greek|Ελληνικά|Grek
en|English|English|Latn
eo|Esperanto|Esperanto|Latn
es|Spanish|español|Latn
et|Estonian|eesti|Latn
eu|Basque|euskara|Latn
fa|Persian|فارسی|Arab
ff|Fulah|Fulfulde|Latn
fi|Finnish|suomi|Latn
fj|Fijian|vosa Vakaviti|Latn
fo|Faroese|føroyskt|Latn
fr|French|français|Latn
fy|Western Frisian|Frysk|Latn
ga|Irish|Gaeilge|Latn
gd|Scottish Gaelic|Gàidhlig|Latn
gl|Galician|galego|Latn
gn|Guarani|Avañe'ẽ|Latn
gu|Gujarati|ગુજરાતી|Gujr
gv|Manx|Gaelg|Latn
ha|Hausa|Hausa|Latn
he|Hebrew|עברית|Hebr
hi|Hindi|हिन्दी|Deva
ho|Hiri Motu|Hiri Motu|Latn
hr|Croatian|hrvatski|Latn
ht|Haitian Creole|Kreyòl ayisyen|Latn
hu|Hungarian|magyar|Latn
hy|Armenian|Հայերեն|Armn
hz|Herero|Otjiherero|Latn
ia|Interlingua|Interlingua|Latn
id|Indonesian|Bahasa Indonesia|Latn
ie|Interlingue|Interlingue|Latn
ig|Igbo|Asụsụ Igbo|Latn
ii|Sichuan Yi|ꆈꌠꉙ|Yiii
ik|Inupiaq|Iñupiatun|Latn
io|Ido|Ido|Latn
is|Icelandic|íslenska|Latn
it|Italian|italiano|Latn
iu|Inuktitut|ᐃᓄᒃᑎᑐᑦ|Cans
ja|Japanese|日本語|Jpan
jv|Javanese|basa Jawa|Latn
ka|Georgian|ქართული|Geor
kg|Kongo|Kikongo|Latn
ki|Kikuyu|Gĩkũyũ|Latn
kj|Kuanyama|Kuanyama|Latn
kk|Kazakh|қазақ тілі|Cyrl
kl|Kalaallisut|kalaallisut|Latn
km|Khmer|ខ្មែរ|Khmr
kn|Kannada|ಕನ್ನಡ|Knda
ko|Korean|한국어|Kore
kr|Kanuri|Kanuri|Latn
ks|Kashmiri|كٲشُر|Arab
ku|Kurdish|Kurdî|Latn
kv|Komi|коми кыв|Cyrl
kw|Cornish|Kernewek|Latn
ky|Kyrgyz|кыргызча|Cyrl
la|Latin|latine|Latn
lb|Luxembourgish|Lëtzebuergesch|Latn
lg|Ganda|Luganda|Latn
li|Limburgish|Limburgs|Latn
ln|Lingala|lingála|Latn
lo|Lao|ລາວ|Laoo
lt|Lithuanian|lietuvių|Latn
lu|Luba-Katanga|Kiluba|Latn
lv|Latvian|latviešu|Latn
mg|Malagasy|Malagasy|Latn
mh|Marshallese|Kajin M̧ajeļ|Latn
mi|Maori|te reo Māori|Latn
mk|Macedonian|македонски|Cyrl
ml|Malayalam|മലയാളം|Mlym
mn|Mongolian|монгол|Cyrl
mr|Marathi|मराठी|Deva
ms|Malay|Bahasa Melayu|Latn
mt|Maltese|Malti|Latn
my|Burmese|မြန်မာ|Mymr
na|Nauru|Dorerin Naoero|Latn
nb|Norwegian Bokmål|norsk bokmål|Latn
nd|North Ndebele|isiNdebele|Latn
ne|Nepali|नेपाली|Deva
ng|Ndonga|Owambo|Latn
nl|Dutch|Nederlands|Latn
nn|Norwegian Nynorsk|norsk nynorsk|Latn
no|Norwegian|norsk|Latn
nr|South Ndebele|isiNdebele|Latn
nv|Navajo|Diné bizaad|Latn
ny|Chichewa|Chichewa|Latn
oc|Occitan|occitan|Latn
oj|Ojibwe|ᐊᓂᔑᓈᐯᒧᐎᓐ|Cans
om|Oromo|Afaan Oromoo|Latn
or|Odia|ଓଡ଼ିଆ|Orya
os|Ossetian|ирон|Cyrl
pa|Punjabi|ਪੰਜਾਬੀ|Guru
pi|Pali|पाऴि|Deva
pl|Polish|polski|Latn
ps|Pashto|پښتو|Arab
pt|Portuguese|português|Latn
qu|Quechua|Runa Simi|Latn
rm|Romansh|rumantsch|Latn
rn|Kirundi|Ikirundi|Latn
ro|Romanian|română|Latn
ru|Russian|русский|Cyrl
rw|Kinyarwanda|Ikinyarwanda|Latn
sa|Sanskrit|संस्कृतम्|Deva
sc|Sardinian|sardu|Latn
sd|Sindhi|سنڌي|Arab
se|Northern Sami|davvisámegiella|Latn
sg|Sango|yângâ tî sängö|Latn
si|Sinhala|සිංහල|Sinh
sk|Slovak|slovenčina|Latn
sl|Slovenian|slovenščina|Latn
sm|Samoan|gagana Sāmoa|Latn
sn|Shona|chiShona|Latn
so|Somali|Soomaali|Latn
sq|Albanian|shqip|Latn
sr|Serbian|српски|Cyrl
ss|Swati|siSwati|Latn
st|Southern Sotho|Sesotho|Latn
su|Sundanese|basa Sunda|Latn
sv|Swedish|svenska|Latn
sw|Swahili|Kiswahili|Latn
ta|Tamil|தமிழ்|Taml
te|Telugu|తెలుగు|Telu
tg|Tajik|тоҷикӣ|Cyrl
th|Thai|ไทย|Thai
ti|Tigrinya|ትግርኛ|Ethi
tk|Turkmen|Türkmençe|Latn
tl|Tagalog|Tagalog|Latn
tn|Tswana|Setswana|Latn
to|Tongan|lea faka-Tonga|Latn
tr|Turkish|Türkçe|Latn
ts|Tsonga|Xitsonga|Latn
tt|Tatar|татар теле|Cyrl
tw|Twi|Twi|Latn
ty|Tahitian|reo Tahiti|Latn
ug|Uyghur|ئۇيغۇرچە|Arab
uk|Ukrainian|українська|Cyrl
ur|Urdu|اردو|Arab
uz|Uzbek|oʻzbekcha|Latn
ve|Venda|Tshivenḓa|Latn
vi|Vietnamese|Tiếng Việt|Latn
vo|Volapük|Volapük|Latn
wa|Walloon|walon|Latn
wo|Wolof|Wolof|Latn
xh|Xhosa|isiXhosa|Latn
yi|Yiddish|ייִדיש|Hebr
yo|Yoruba|Èdè Yorùbá|Latn
za|Zhuang|Vahcuengh|Latn
zh|Chinese|中文|Hans
zu|Zulu|isiZulu|Latn
"""


# The game supports exactly these 16 languages (user decision 2026-11-25). DATA keeps every ISO 639-1 language so
# one can be added later: add its code here, add Resources/Loc/<code>.json and its font, and re-run this script.
SUPPORTED = {"en", "th", "zh", "ja", "ko", "es", "pt", "fr", "de", "ru", "it", "tr", "id", "vi", "ms", "tl"}


def main():
    rows = []
    for line in DATA.strip().splitlines():
        code, name, native, script = line.split("|")
        if code not in SUPPORTED:
            continue
        rows.append({"code": code, "name": name, "native": native, "script": script,
                     "rtl": script in RTL_SCRIPTS,
                     "translated": code == "en" or os.path.exists(os.path.join(LOC, code + ".json"))})
    codes = [r["code"] for r in rows]
    assert len(codes) == len(set(codes)), "duplicate code"
    rows.sort(key=lambda r: r["name"].lower())
    with open(os.path.join(LOC, "languages.json"), "w", encoding="utf-8", newline="\n") as f:
        f.write('{\n  "languages": [\n')
        f.write(",\n".join("    " + json.dumps(r, ensure_ascii=False) for r in rows))
        f.write("\n  ]\n}\n")
    print(len(rows), "languages,", sum(r["translated"] for r in rows), "translated")


if __name__ == "__main__":
    main()
