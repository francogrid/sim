# Installation
## Récupération des sources depuis le dépôt
Pour commencer on récupère une copie du dépot:
    git clone git://github.com/francogrid/sim.git
Un répertoire sim/ contenant les sources est créé.
Cette étape n'est à réaliser qu'une seule fois si vous conservez le répertoire sim/ après chaque compilation.
## Compilation
On entre dans le répertoire sim/ et on lance la compilation:
    cd sim
    ./runprebuild.sh
    nant
Le répertoire bin/ est alors prêt à être utiliser pour lancer OpenSim. Il est préférable d'en faire une copie, à l'endroit de votre choix.
## Mises à jour
Pour faire une mise à jour, il suffit de répéter les 3 étapes suivantes:
### Nettoyage de la précédente compilation
On entre dans le répertoire sim/ et on demande à nettoyer les fichiers qui ont été générés pendant la précédente compilation:
    cd sim
    nant clean
### Récupération des dernières sources
On récupère les dernières modifications des sources d'OpenSim pour FrancoGrid:
    git pull origin master
### Compilation
Enfin à nouveau, on compile:
    ./runprebuild.sh
    nant
Copiez bin/ à l'endroit voulu, et ainsi de suite...
