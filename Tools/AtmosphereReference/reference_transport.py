"""Independent double-precision spherical-atmosphere transport.

No Unity shader, Unity LUT, or previously exported sky is used in this module.
Inputs are physical profiles/coefficients. Direct paths use Gauss quadrature;
single scatter uses a converged optical-depth integral; multiple scatter uses
null-collision volume path tracing and next-event estimation of a delta Sun.
The solid planet is black. RGB channels are transported independently.
"""
import os,sys
from pathlib import Path
import numpy as np

extra=Path(os.environ.get('TEMP','.'))/'maxsrp-atmosphere-python'
if extra.is_dir():sys.path.insert(0,str(extra))
from numba import njit,prange,set_num_threads
set_num_threads(min(4,os.cpu_count() or 1))

# p = radius, height, H_R, H_M, g, ozone centre/width,
#     beta_R[3], beta_M_scatter, beta_M_absorb, beta_O[3] (all per metre).
def parameters(settings):
    return np.array([settings['_PlanetRadius'],settings['_AtmosphereHeight'],
        settings['_RayleighScatteringScalarHeight'],settings['_MieScatteringScalarHeight'],
        settings['_MieAnisotropy'],settings['_OzoneLevelCenterHeight'],settings['_OzoneLevelWidth'],
        * (np.array([5.802,13.558,33.1])*1e-6*settings['_RayleighScatteringScale']),
        3.996e-6*settings['_MieScatteringScale'],.444e-6*settings['_MieScatteringScale'],
        *(np.array([.650,1.881,.085])*1e-6*settings['_OzoneAbsorptionScale'])],np.float64)

@njit(cache=True)
def coefficients(h,ch,p):
    h=max(0.,h)
    r=p[7+ch]*np.exp(-h/p[2]);m=p[10]*np.exp(-h/p[3])
    a=p[11]*np.exp(-h/p[3])+p[12+ch]*max(0.,1-abs(h-p[5])/p[6])
    return r,m,r+m+a

@njit(cache=True)
def phase_r(mu):return 3*(1+mu*mu)/(16*np.pi)
@njit(cache=True)
def phase_m(mu,g):
    return 3*(1-g*g)*(1+mu*mu)/(8*np.pi*(2+g*g)*(1+g*g-2*g*mu)**1.5)

@njit(cache=True)
def boundary(r,mu,p):
    c=(r-p[0])*(r+p[0]);b=r*mu
    top=p[0]+p[1]
    d=-b+np.sqrt(max(0.,b*b+(top-r)*(top+r)))
    disc=b*b-c
    if b<0 and disc>=0:d=min(d,max(0.,-b-np.sqrt(disc)))
    return d

@njit(cache=True)
def blocked(r,mu,p):return mu<0 and r*r*mu*mu-(r-p[0])*(r+p[0])>=0

@njit(cache=True)
def optical_depth(r,mu,length,ch,p,nodes,weights):
    tau=0.
    for j in range(len(nodes)):
        d=(nodes[j]+1)*length*.5
        h=np.sqrt(r*r+d*d+2*r*mu*d)-p[0]
        tau+=weights[j]*coefficients(h,ch,p)[2]
    return tau*length*.5

@njit(cache=True)
def direct_transmission(h,mu,p,nodes,weights):
    r=p[0]+h;out=np.zeros(3)
    if blocked(r,mu,p):return out
    d=boundary(r,mu,p)
    for ch in range(3):out[ch]=np.exp(-optical_depth(r,mu,d,ch,p,nodes,weights))
    return out

@njit(cache=True,parallel=True)
def build_solar_tau_table(p,nodes,weights,nh=256,nu=1024):
    # Independent parameterization: sqrt altitude, log distance above the local
    # horizon. Entries include endpoints, unlike normalized GPU texel centres.
    table=np.empty((nh,nu,3))
    for index in prange(nh*nu):
        iy=index//nu;ix=index%nu
        h=(iy/(nh-1))**2*p[1];r=p[0]+h
        horizon=-np.sqrt(max(0.,(r-p[0])*(r+p[0])))/r
        c=np.expm1(ix/(nu-1)*np.log(101.))/100
        mu=horizon+(1-horizon)*c
        d=-(r*mu)+np.sqrt(max(0.,r*r*mu*mu+(p[0]+p[1]-r)*(p[0]+p[1]+r)))
        for ch in range(3):table[iy,ix,ch]=optical_depth(r,mu,d,ch,p,nodes,weights)
    return table

@njit(cache=True)
def solar_t(r,mu,ch,p,table):
    if blocked(r,mu,p):return 0.
    h=max(0.,min(p[1],r-p[0]));nh,nu,_=table.shape
    horizon=-np.sqrt(max(0.,(r-p[0])*(r+p[0])))/r
    c=max(0.,min(1.,(mu-horizon)/(1-horizon)))
    x=np.log1p(100*c)/np.log(101.)*(nu-1)
    y=np.sqrt(h/p[1])*(nh-1)
    ix=min(nu-2,int(x));iy=min(nh-2,int(y));fx=x-ix;fy=y-iy
    tau=(table[iy,ix,ch]*(1-fx)+table[iy,ix+1,ch]*fx)*(1-fy)
    tau+=(table[iy+1,ix,ch]*(1-fx)+table[iy+1,ix+1,ch]*fx)*fy
    return np.exp(-tau)

@njit(cache=True)
def single_scatter(h,view,sun,p,table,steps=8192):
    r=p[0]+h;mu=view[1];nu=view[0]*sun[0]+view[1]*sun[1]+view[2]*sun[2]
    distance=boundary(r,mu,p);ds=distance/steps
    tau=np.zeros(3);out=np.zeros(3)
    pr=phase_r(nu);pm=phase_m(nu,p[4])
    for i in range(steps):
        s=(i+.5)*ds
        x=view[0]*s;y=r+view[1]*s;z=view[2]*s
        radius=np.sqrt(x*x+y*y+z*z);alt=radius-p[0]
        mus=(x*sun[0]+y*sun[1]+z*sun[2])/radius
        for ch in range(3):
            br,bm,bt=coefficients(alt,ch,p)
            out[ch]+=np.exp(-(tau[ch]+.5*bt*ds))*(br*pr+bm*pm)*solar_t(radius,mus,ch,p,table)*ds
            tau[ch]+=bt*ds
    return out

@njit(cache=True,parallel=True)
def single_irradiance(h,sun,p,table,mus,weights,nphi=96,steps=4096):
    values=np.zeros((len(mus),3))
    for i in prange(len(mus)):
        mu=(mus[i]+1)*.5;sine=np.sqrt(1-mu*mu)
        for j in range(nphi):
            phi=2*np.pi*(j+.5)/nphi
            view=np.array([sine*np.cos(phi),mu,sine*np.sin(phi)])
            values[i]+=single_scatter(h,view,sun,p,table,steps)*(mu*weights[i]*np.pi/nphi)
    return values.sum(axis=0)

@njit(cache=True)
def sample_phase_cosine(choose_rayleigh,g):
    while True:
        if choose_rayleigh:mu=2*np.random.random()-1
        elif abs(g)<1e-8:mu=2*np.random.random()-1
        else:
            k=(1-g*g)/(1-g+2*g*np.random.random())
            mu=max(-1.,min(1.,(1+g*g-k*k)/(2*g)))
        # Both Rayleigh (uniform proposal) and Cornette-Shanks (HG proposal)
        # have an acceptance proportional to 1+mu^2.
        if np.random.random()<.5*(1+mu*mu):return mu

@njit(cache=True)
def scatter_direction(dx,dy,dz,mu):
    if abs(dz)<.9:tx=dy;ty=-dx;tz=0.
    else:tx=-dz;ty=0.;tz=dx
    length=np.sqrt(tx*tx+ty*ty+tz*tz);tx/=length;ty/=length;tz/=length
    bx=dy*tz-dz*ty;by=dz*tx-dx*tz;bz=dx*ty-dy*tx
    phi=2*np.pi*np.random.random();a=np.sqrt(max(0.,1-mu*mu))*np.cos(phi);b=np.sqrt(max(0.,1-mu*mu))*np.sin(phi)
    return dx*mu+a*tx+b*bx,dy*mu+a*ty+b*by,dz*mu+a*tz+b*bz

@njit(cache=True,parallel=True)
def path_trace(h,view,sun,p,table,blocks,paths_per_block,seed,hemisphere=False,max_orders=24):
    # Independent hero RGB paths; each row is one separately seeded batch.
    batches=np.zeros((blocks,3,max_orders))
    for task in prange(blocks*3):
        batch=task//3;ch=task%3
        np.random.seed(seed+task*7919)
        majorant=p[7+ch]+p[10]+p[11]+p[12+ch]
        if majorant==0:continue
        for sample in range(paths_per_block):
            if hemisphere:
                mu=np.sqrt(np.random.random());s=np.sqrt(1-mu*mu);phi=2*np.pi*np.random.random()
                dx=s*np.cos(phi);dy=mu;dz=s*np.sin(phi)
            else:dx=view[0];dy=view[1];dz=view[2]
            x=0.;y=p[0]+h;z=0.;weight=1.
            for order in range(max_orders):
                radius=np.sqrt(x*x+y*y+z*z)
                distance=boundary(radius,(x*dx+y*dy+z*dz)/radius,p)
                hit=False;br=0.;bm=0.;bt=0.
                while True:
                    step=-np.log(max(np.random.random(),1e-300))/majorant
                    if step>=distance:break
                    x+=dx*step;y+=dy*step;z+=dz*step;distance-=step
                    radius=np.sqrt(x*x+y*y+z*z)
                    br,bm,bt=coefficients(radius-p[0],ch,p)
                    if np.random.random()*majorant<bt:hit=True;break
                if not hit:break
                bs=br+bm
                if bs==0:break
                weight*=bs/bt
                mus=(x*sun[0]+y*sun[1]+z*sun[2])/radius
                nu=max(-1.,min(1.,dx*sun[0]+dy*sun[1]+dz*sun[2]))
                pdf=(br*phase_r(nu)+bm*phase_m(nu,p[4]))/bs
                batches[batch,ch,order]+=weight*pdf*solar_t(radius,mus,ch,p,table)
                mu=sample_phase_cosine(np.random.random()*bs<br,p[4])
                dx,dy,dz=scatter_direction(dx,dy,dz,mu)
                if order>=8:
                    if np.random.random()>.8:break
                    weight/=.8
        batches[batch,ch]/=paths_per_block
        if hemisphere:batches[batch,ch]*=np.pi
    return batches
