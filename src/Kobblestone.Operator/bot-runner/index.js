const mineflayer = require('mineflayer');
const http = require('http');
const fs = require('fs');
const crypto = require('crypto');
const config = require('/etc/bot/config.json');

const SESSION_PATH = '/etc/bot/session/session.json';
const PROFILE_PATH = '/etc/bot/profile/profile.json';
const CERTS_PATH = '/etc/bot/certs/certificates.json';
const ENTITLEMENTS_PATH = '/etc/bot/entitlements/entitlements.json';

const session = fs.existsSync(SESSION_PATH) ? JSON.parse(fs.readFileSync(SESSION_PATH, 'utf8')) : null;

const profile = fs.existsSync(PROFILE_PATH) ? JSON.parse(fs.readFileSync(PROFILE_PATH, 'utf8')) : null;

const certs = fs.existsSync(CERTS_PATH) ? JSON.parse(fs.readFileSync(CERTS_PATH, 'utf8')) : null;

const entitlements = fs.existsSync(ENTITLEMENTS_PATH) ? JSON.parse(fs.readFileSync(ENTITLEMENTS_PATH, 'utf8')) : null;

function reviveCertificates(c) {
    const normalizePem = (pem) =>
        pem.replace('BEGIN RSA PUBLIC KEY', 'BEGIN PUBLIC KEY')
            .replace('END RSA PUBLIC KEY', 'END PUBLIC KEY')
            .replace('BEGIN RSA PRIVATE KEY', 'BEGIN PRIVATE KEY')
            .replace('END RSA PRIVATE KEY', 'END PRIVATE KEY');

    return {
        ...c, profileKeys: {
            ...c.profileKeys, // KeyObjects can't be JSON'd — rebuild from the PEMs
            public: crypto.createPublicKey(normalizePem(c.profileKeys.publicPEM)),
            private: crypto.createPrivateKey(normalizePem(c.profileKeys.privatePEM)), // any base64-serialized Buffer fields your account-auth stored:
            expiresOn: new Date(c.profileKeys.expiresOn),
            ...(c.profileKeys.publicDER && {publicDER: Buffer.from(c.profileKeys.publicDER, 'base64')}), ...(c.profileKeys.privateDER && {privateDER: Buffer.from(c.profileKeys.privateDER, 'base64')}), ...(c.profileKeys.signature && {signature: Buffer.from(c.profileKeys.signature, 'base64')}), ...(c.profileKeys.signatureV2 && {signatureV2: Buffer.from(c.profileKeys.signatureV2, 'base64')}),
        },
    };
}

const options = {
    host: config.host, port: config.port, username: config.username, ...(session && {
        auth: (client, opts) => {
            client.username = session.name;
            client.uuid = session.uuid;
            client.session = {
                accessToken: session.accessToken, selectedProfile: {
                    id: session.uuid, name: config.username,
                }
            }
            opts.accessToken = session.accessToken;
            opts.haveCredentials = true;

            if (certs) {
                Object.assign(client, reviveCertificates(certs));
            }

            client.emit('session', client.session);
            opts.connect(client);
        },
    }),
}

const bot = mineflayer.createBot(options);

let healthy = false;

bot.once('spawn', () => {
    healthy = true;
    console.log('spawned');

    if (config.viewer) {
        const {mineflayer: startViewer} = require('prismarine-viewer');
        startViewer(bot, {
            port: 3000, firstPerson: config.viewer.firstPerson, viewDistance: config.viewer.viewDistance,
        });
        console.log(`viewer listening on :${config.viewer.port}`);
    }
});

bot.on('end', (reason) => {
    console.log(`disconnected: ${reason}`);
    process.exit(1);
});
bot.on('kicked', (reason) => console.error(`kicked: ${JSON.stringify(reason)}`));
bot.on('error', (err) => console.error(err));

for (const b of config.behaviors) {
    const factory = require(`./behaviors/${b.name}/index.js`);
    bot.loadPlugin(factory(b.params));
    console.log(`loaded behavior: ${b.name}`);
}

http.createServer((req, res) => {
    res.writeHead(healthy ? 200 : 503).end(healthy ? 'ok' : 'not spawned');
}).listen(8080);
