TOPICS = basics syntax control-flow collections filesystem units interop

.PHONY: all clean

all:
	set -e; for topic in $(TOPICS); do \
		latexmk -cd -pdf -interaction=nonstopmode -halt-on-error $$topic/$$topic.tex; \
	done

clean:
	set -e; for topic in $(TOPICS); do \
		latexmk -cd -C $$topic/$$topic.tex; \
	done
