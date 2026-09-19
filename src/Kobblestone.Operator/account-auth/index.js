const https = require('https');
const fs = require('fs');
const {Authflow, Titles} = require('prismarine-auth');

const {ACCOUNT_NAME, NAMESPACE, SECRET_NAME} = process.env;

const SA = '/var/run/secrets/kubernetes.io/serviceaccount';
const CA = fs.readFileSync(`${SA}/ca.crt`);

function k8s(method, path, body, contentType = 'application/json') {
    const token = fs.readFileSync(`${SA}/token`, 'utf8');
    return new Promise((resolve, reject) => {
        const req = https.request({
            host: process.env.KUBERNETES_SERVICE_HOST,
            port: process.env.KUBERNETES_SERVICE_PORT,
            path,
            method,
            ca: CA,
            headers: {Authorization: `Bearer ${token}`, 'Content-Type': contentType},
        }, res => {
            let d = '';
            res.on('data', c => d += c);
            res.on('end', () => res.statusCode < 300 ? resolve(d ? JSON.parse(d) : null) : reject(new Error(`${method} ${path} -> ${res.statusCode}: ${d}`)));
        });
        req.on('error', reject);
        req.end(body ? JSON.stringify(body) : undefined);
    });
}

const SECRET_PATH = `/api/v1/namespaces/${NAMESPACE}/secrets/${SECRET_NAME}`;

const getSecret = () => k8s('GET', SECRET_PATH);
const patchSecretKeys = (stringData) => k8s('PATCH', SECRET_PATH, {stringData}, 'application/merge-patch+json');
const patchAccountStatus = (status) => k8s('PATCH', `/apis/kobblestone.io/v1alpha1/namespaces/${NAMESPACE}/accounts/${ACCOUNT_NAME}/status`, {status}, 'application/merge-patch+json');

class SecretCache {
    constructor(cacheName) {
        this.key = `cache-${cacheName}.json`;
    }

    async getCached() {
        const secret = await getSecret();
        const raw = secret.data?.[this.key];
        return raw ? JSON.parse(Buffer.from(raw, 'base64').toString('utf8')) : {};
    }

    async setCached(value) {
        await patchSecretKeys({[this.key]: JSON.stringify(value)});
    }

    async setCachedPartial(value) {
        await this.setCached({...(await this.getCached()), ...value});
    }
}

async function authorize() {
    const onMsaCode = async (dc) => {
        console.log(`device flow: ${dc.verification_uri} code ${dc.user_code}`);

        const now = Date.now();

        await patchAccountStatus({
            phase: 'AwaitingAuthorization', deviceFlow: {
                userCode: dc.user_code,
                verificationUri: dc.verification_uri,
                expiresAt: new Date(now + dc.expires_in * 1000).toISOString(),
                message: dc.message
            },
            message: dc.message,
            conditions: [
                {
                    type: "Authorized",
                    status: "False",
                    lastTransitionTime: new Date(now).toISOString(),
                    reason: "AwaitingAuthorization",
                    message: dc.message
                },
                {
                    type: "AwaitingAuthorization",
                    status: "True",
                    lastTransitionTime: new Date(now).toISOString(),
                    message: dc.message
                }
            ]
        });
    };

    const flow = new Authflow(ACCOUNT_NAME, ({cacheName}) => new SecretCache(cacheName), {
        flow: 'live', authTitle: Titles.MinecraftNintendoSwitch, deviceType: 'Nintendo'
    }, onMsaCode,);

    return flow.getMinecraftJavaToken({fetchProfile: true, fetchCertificates: true, fetchEntitlements: true});
}

function serializeCertificates(certificates) {
    if (!certificates?.profileKeys) return null;

    const toJsonSafe = (v) => {
        if (Buffer.isBuffer(v)) return v.toString('base64');
        if (v instanceof Date) return v.toISOString();
        return v;
    };

    const {public: _pub, private: _priv, ...rest} = certificates.profileKeys;

    return {
        ...certificates,
        profileKeys: Object.fromEntries(Object.entries(rest).map(([k, v]) => [k, toJsonSafe(v)])), ...(certificates.expiresOn && {expiresOn: toJsonSafe(certificates.expiresOn)}), ...(certificates.refreshAfter && {refreshAfter: toJsonSafe(certificates.refreshAfter)}),
    };
}

async function main() {
    const {token, profile, entitlements, certificates} = await authorize();

    const now = Date.now();

    const expiresAt = new Date(now + 23 * 3600 * 1000);

    const certsJson = serializeCertificates(certificates);

    await patchSecretKeys({
        'session.json': JSON.stringify({
            accessToken: token, uuid: profile.id, name: profile.name, expiresAt: expiresAt.toISOString(),
        }),
        'profile.json': JSON.stringify(profile),
        'entitlements.json': JSON.stringify(entitlements), ...(certsJson && {
            'certificates.json': JSON.stringify(certsJson),
        }),
    });

    await patchAccountStatus({
        phase: 'Authorized',
        deviceFlow: null,
        authorizedAt: new Date(now).toISOString(),
        sessionExpiresAt: expiresAt.toISOString(),
        profile: {name: profile.name, uuid: profile.id},
        message: 'Successfully authorized',
        conditions: [
            {
                type: "Authorized",
                status: "True",
                lastTransitionTime: new Date(now).toISOString(),
                reason: "DeviceFlowCompleted",
                message: "Account successfully authorized by completed device flow."
            },
            {
                type: "AwaitingAuthorization",
                status: "False",
                lastTransitionTime: new Date(now).toISOString(),
                reason: "DeviceFlowCompleted"
            }
        ]
    });

    console.log(`authenticated as ${profile.name} (${profile.id})`);
}

main().catch(async (err) => {
    console.error(err.stack ?? err.message);

    await patchAccountStatus({
        phase: 'Unauthorized',
        deviceFlow: null,
        authorizedAt: null,
        sessionExpiresAt: null,
        profile: null,
        message: err.stack ?? err.message
    });

    process.exit(1);
});