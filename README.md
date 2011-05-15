# Installation
## La première fois
### Initialisation d'un dépôt vide en local
	mkdir beta
	cd beta
	git init
### Merge de la branche beta-0.7.1-dev

	git remote add francogrid git://github.com/francogrid/sim.git
	git pull francogrid beta-0.7.1-dev
### Compilation
	./runprebuild.sh
	nant
puis copier le répertoire bin/ à l'endroit voulu.
## Mises à jour
### nettoyage de la précédente compilation
	cd beta
	nant clean
### récupération des dernières sources
	git pull francogrid beta-0.7.1-dev
et compilation etc.
