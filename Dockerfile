FROM didstopia/rust-server:latest

ADD rustarena.sh /app/rustarena.sh

CMD [ "bash", "/app/rustarena.sh"]
