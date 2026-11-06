# Neměřená fáze sítě

*English: [UNMETERED_GRID_PHASE.md](UNMETERED_GRID_PHASE.md)*

**Stručně: nechte pole prázdné.** Vyplňte ho jen tehdy, když elektroměr vaší fotovoltaické elektrárny
neměří jednu ze tří fází. U správně zapojené instalace by nastavení věci nezlepšilo, ale zhoršilo.

## O co jde

Třífázový hybridní střídač SolaX má v místě, kde se dům připojuje k síti, **elektroměr**: chytrý
elektroměr nebo měřicí kleště, které na každé fázi — L1, L2 a L3 (SolaX je označuje R, S a T) — měří,
kolik energie teče ze sítě do domu, nebo z domu do sítě.

Podle tohoto elektroměru Gleanvolt rozhoduje, kdy nabíjet auto. Přebytek je solární energie, která by
jinak odtekla do sítě. Vlastní spotřeba domu se nikde neměří, ale dopočítává se:

> dům = slunce + síť − auto − domácí baterie

Vše proto závisí na tom, aby údaj ze sítě byl úplný.

## Co se pokazí, když se jedna fáze neměří

Pokud na jedné fázi měřicí kleště chybí, jsou odpojené nebo nasazené na špatném vodiči, elektroměr na ní
**ukazuje 0 W, ať tam teče cokoli**. Vše, co se na té fázi děje, je neviditelné:

- **Není vidět solární energie, kterou střídač na té fázi posílá do sítě.** Střídač rozkládá výkon do
  všech tří fází, takže zhruba třetina přetoků zmizí. Gleanvolt usoudí, že ji spotřebuje dům — spotřeba
  domu jako by sledovala slunce — a nenajde skoro žádný přebytek. Solární režimy pak auto nespustí ani
  za slunečného odpoledne s plnou domácí baterií.
- **Není vidět odběr auta na té fázi.** Auto nabíjející třemi fázemi bere třetinu výkonu přes slepou
  fázi. Gleanvolt vidí menší odběr ze sítě, než ve skutečnosti je, myslí si, že auto pokrývá slunce, a
  nabíjení potichu bere energii ze sítě.

## Proč toto nastavení vzniklo: skutečný případ

Nastavení pochází z instalace, na které se Gleanvolt vyvíjel: třífázový hybridní střídač SolaX
s domácí baterií a wallbox SolaX. Elektroměr té instalace nemá na fázi **L3** (u SolaX „T“) funkční
měřicí kleště, takže na ní ukazuje vždy 0 W — přitom střídač sám hlásí, že za slunečného odpoledne
posílá do L3 **1,7–2,3 kW**.

Jak to vypadalo navenek:

- **Každé slunečné odpoledne to vypadalo, že dům spotřebuje asi 90 % solární výroby** — nejméně dvanáct
  dní po sobě. Přetoky na L3 se nikdy neukázaly jako dodávka do sítě, a tak se započítaly jako spotřeba
  domu.
- **Solární režimy auto nenabíjely.** SolarGrid se při přechodu mraku pozastavil a už se znovu
  nespustil, i když ze střechy šlo 4–5 kW. Podle toho, co Gleanvolt viděl, žádný přebytek nebyl.
- **Během nabíjení auta to vypadalo, že dům několik kilowattů *vyrábí*.** Třetina odběru auta šla
  neviděna přes L3, takže odběr ze sítě vypadal menší než samotné auto a spotřeba domu vyšla záporná.

Nejdřív to vypadalo na skrytý spotřebič, třeba regulátor přetoků do bojleru, který si potichu bere
přebytek. Nebyl to on: porovnání údajů elektroměru po fázích s výkonem střídače po fázích ukázalo, že
jedna fáze stojí na 0 W, zatímco střídač do ní zjevně dodává.

Správným řešením je doplnit měřicí kleště, na to je ale potřeba elektrikář. Do té doby díky tomuto
nastavení Gleanvolt chybějící fázi odhadne a solární režimy fungují.

## Příznaky, že elektroměr jednu fázi nevidí

- Slunečné odpoledne, plná domácí baterie, a přesto Gleanvolt ukazuje vysokou spotřebu domu a skoro
  žádný přebytek a režim SolarGrid nebo Forecasted nabíjení nespustí.
- Spotřeba domu na Přehledu stoupá a klesá se solární výrobou.
- Během nabíjení auta je u spotřeby domu pomlčka (vyšla pod nulou), nebo je odběr ze sítě menší, než
  kolik bere samotné auto.
- Vyúčtování ukazuje větší odběr ze sítě, než hlásí střídač nebo Gleanvolt.

## Jak to ověřit

Podívejte se na údaje elektroměru **po jednotlivých fázích**, když svítí slunce — na displeji střídače,
v aplikaci SolaX Cloud, nebo se zeptejte instalačního technika.

- **Elektroměr zapojený správně:** všechny tři fáze ukazují hodnoty, které se v čase mění.
- **Slepá fáze:** jedna fáze ukazuje pořád **0 W**, zatímco výkon střídače na téže fázi nulový zjevně
  není.

Pro technicky zdatné: přes Modbus jsou údaje elektroměru po fázích v registrech `0x82`/`0x84`/`0x86`,
výkon střídače po fázích v registrech `0x6C`/`0x70`/`0x74`. Slepá fáze ukazuje 0 W v první trojici,
ale ne ve druhé.

Pokud to sami ověřit nemůžete, nechte pole prázdné a zeptejte se instalačního technika.

## Co zadat

Fázi, která ukazuje 0 W: `L1`, `L2` nebo `L3`, případně označení SolaX `R`, `S` nebo `T` (R = L1,
S = L2, T = L3). Uložte a restartujte Gleanvolt na stránce Stav.

## Co s tím Gleanvolt udělá

Tok na slepé fázi odhadne z toho, co zná:

- výkon střídače na té fázi, který střídač hlásí sám;
- podíl auta, za předpokladu, že auto odebírá rovnoměrně ze všech fází, kterými nabíjí (auto nabíjející
  jednou fází se bere jako připojené na L1);
- spotřebu domu na té fázi, odhadnutou jako polovinu toho, co nesou dvě měřené fáze.

Je to odhad: jeden velký spotřebič na slepé fázi — trouba, rychlovarná konvice — ho na chvíli vychýlí.
Přesto je mnohem blíž skutečnosti, než kdyby se fáze vynechala. Všechny údaje o síti — Přehled, historie,
Home Assistant — pak používají odhad; log vedle něj vypisuje i hodnotu z elektroměru.

## Skutečné řešení

Nechte instalačního technika měřicí kleště doplnit nebo opravit. Jakmile elektroměr měří všechny tři
fáze, **pole vyprázdněte a restartujte**. Kdyby zůstalo vyplněné u elektroměru, který vidí všechny fáze,
započítala by se ta fáze dvakrát a rozhodování o nabíjení by se pokazilo opačným směrem.
